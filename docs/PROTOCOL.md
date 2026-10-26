# SensorDeck collector protocol

How the UI (or any client) talks to the collector service. Implementation: `src/SensorDeck.Core/Protocol`, server in `src/SensorDeck.Collector/Pipe`.

## Transport

- Named pipe **`\\.\pipe\SensorDeck`**, duplex, byte mode, local machine only.
- Access: any authenticated (signed-in) user can connect, read and write. Only SYSTEM, administrators and the account running the collector can create pipe instances. The first instance uses `FirstPipeInstance`, so the collector refuses to start if another process already owns the name.
- **Clients check the server** (`SensorDeck.Core.Protocol.PipeServerIdentity`, used by the app and `sensordeck-cli connect`). While the service is stopped any local program could create the pipe name and feed forged readings (which drive alerts, ntfy pushes and automation), so right after connecting the client gets the server's process id (`GetNamedPipeServerProcessId`) and accepts it only if:
  - **Normal pipe, service has a process** (running, starting or stopping): the pid equals the `SensorDeckCollector` service's process id (`QueryServiceStatusEx`, `SERVICE_QUERY_STATUS` only). Nothing else is accepted then.
  - **Normal pipe, service stopped or not installed** (developer running `dotnet run --project src/SensorDeck.Collector` from an elevated terminal): the server's image (`QueryFullProcessImageName`) is `SensorDeck.Collector.exe` under `%ProgramFiles%`, `%ProgramFiles(x86)%`, or `<root>\src\SensorDeck.Collector\bin\` — the last only when the client itself runs from `<root>\src\<its project>\bin\` — **and** the process is elevated: `TokenElevation` if its token can be opened for query, or, when opening the token is refused with access denied (normal for an elevated process seen from an unelevated client), it runs in the client's own Windows session (another user's processes never do, and an unelevated process of this user always has a readable, non-elevated token). Network paths and `.`/`..` segments are never trusted.
  - **Replay pipe** (`SensorDeck-replay-<pid>`): the pid equals the replay collector the app itself started. Any other pipe name is refused.
  - Anything that can't be checked counts as a mismatch. On a mismatch the client disconnects without reading a frame, shows "Another program is answering on SensorDeck's connection — not the SensorDeck collector service. Data is not shown." (banner and Diagnostics → Connection), logs it once to `%AppData%\SensorDeck\app-errors.log`, and retries on the normal 2 s schedule, so the real service is picked up once it starts. `sensordeck-cli connect --no-verify` skips the check (e.g. to look at a replay pipe).
- Up to 32 clients at once; a further client is disconnected right after connecting until a slot frees up.
- Clients are untrusted. The collector drops a client that sends a frame over 1 MiB or doesn't read a frame within 30 s (it stopped reading, and its answers would pile up). At most 8 history requests per client are answered at a time; while 8 answers are outstanding the collector stops reading that client's requests until one is sent (the client's writes wait; it isn't dropped). Across all clients at most 16 answers are being prepared or waiting to be sent; further requests wait their turn (they are not dropped).

## Framing

Each message is one frame:

```
[ 4-byte little-endian payload length ][ MessagePack payload ]
```

- The payload is an `IProtocolMessage` serialized as a MessagePack **union** (`[union key, object]`), with `MessagePackSecurity.UntrustedData`.
- Maximum payload: 16 MiB (server → client). Client → server frames are limited to 1 MiB. A larger length is a protocol error and the connection is dropped.
- A clean end of stream between frames means the peer disconnected.

## Messages

Union keys and field keys are wire format: **append only, never renumber.** Bump `ProtocolInfo.Version` on incompatible changes.

| Key | Message | Direction | When |
|---|---|---|---|
| 0 | `HelloMessage(ProtocolVersion, ServerVersion, PollIntervalMs)` | server → client | Always the first frame. |
| 1 | `CatalogMessage(Catalog)` | server → client | After Hello, and whenever hardware is added or removed. |
| 2 | `ValuesMessage(CatalogVersion, Sequence, TimestampUnixMs, Values[], Min[], Max[])` | server → client | Every poll. Arrays are in catalog order. |
| 3 | `HistoryRequest(RequestId, SensorIds[], WindowSeconds, FromUnixMs?, ToUnixMs?, MaxPoints)` | client → server | Any time. Up to 512 ids. Without From/To/MaxPoints (v1/v2): the last `WindowSeconds` (≤ 86,400) from memory. With them (v3): any range — memory if it reaches back that far, else disk (raw files, or per-minute files for long spans / older than the raw retention); `MaxPoints` > 0 averages into ≤ that many points (max 5000). A range covers at most 366 days (an earlier `FromUnixMs` is raised); chart requests read at most 32 million values from disk before averaging (per-minute rows are used when every sample would be more). |
| 4 | `HistoryResponse(RequestId, TimestampsUnixMs[], SensorIds[], Values[][], Min[][]?, Max[][]?, NextFromUnixMs?)` | server → client | Reply to a HistoryRequest. `Values[i]` is parallel to `TimestampsUnixMs`; unknown ids get all-null series. Min/Max are set when points summarize several samples. Answers are capped at 1,000,000 values; `NextFromUnixMs` then says where to ask again. |
| 5 | `SetConfigRequest(PollIntervalMs?, StorageEnabled?, NetworkEnabled?, ControllersEnabled?, Persist)` | client → server | Null fields are left unchanged. Interval clamped to 250–5000 ms. Toggling storage/network/controllers reopens the sources and produces a new catalog (at most once every 5 s; later changes wait for the next slot). `Persist = true` also writes the values into the `Collector` section of `collector.json` (other keys kept) so they survive a restart. |
| 6 | `SourceStatusMessage(Sources[])` | server → client | After Hello, and whenever a source's status or message changes. |
| 7 | `CollectorStatusMessage(PollIntervalMs, StorageEnabled, NetworkEnabled, ControllersEnabled, HistorySeconds, IsService, IsElevated, ConfigPath, ConfigError?, StartedUnixMs, MinMaxResetUnixMs?, DiskHistory)` | server → client | After Hello, after every applied `SetConfigRequest` and after a min/max reset. `ConfigError` is set when saving `collector.json` failed. *(v2; last three fields v3)* |
| 8 | `ResetMinMaxRequest(SensorIds[]?)` | client → server | Restart min/max for these sensors (null = all; that also sets `MinMaxResetUnixMs`). Requests arriving between two polls are merged (at most 10,000 ids). *(v3)* |

**Versions:** v1 = keys 0–6. v2 adds key 7 and the `ControllersEnabled`/`Persist` fields on key 5. v3 adds key 8 and the long-history fields. The app compares `HelloMessage.ProtocolVersion` with its own and warns on the Diagnostics page. Since v3, `FrameCodec.ReadAsync` returns `UnknownMessage` for a frame it can't decode (unknown union key from a newer peer) and readers skip it, so mismatched versions no longer drop the connection. (A v2 app still can't read v3-only messages, but the server never sends key 8.)

## Disk history (v3)

`%ProgramData%\SensorDeck\history\`:
- `raw-yyyyMMdd-HH-<hash>.sdh` — every sample, one file per UTC hour, kept `DiskRawHours` (24).
- `min-yyyyMMdd-<hash>.sdh` — min/avg/max per minute, one file per UTC day, kept `DiskMinuteDays` (7).
- Format: `"SDH1"`, Int32 tier, Int32 sensor count, the sensor ids (.NET BinaryWriter strings), then fixed-size little-endian records: Int64 Unix ms + Float32 per sensor (raw) or 3 × Float32 per sensor (minute; NaN = no reading). `<hash>` = FNV-1a of the id list; a catalog change starts a new file.
- Size cap `DiskHistoryMaxMB` (1024): oldest raw files are deleted first. About 90 MB per day of raw data for ~260 sensors at 1 s.

**Permissions:** the pipe ACL lets any signed-in user send `SetConfigRequest`, and with `Persist` the service (running as SYSTEM) writes `%ProgramData%\SensorDeck\collector.json` (only the four known settings, as numbers/booleans). Acceptable for a single-user home PC; revisit if multi-user support matters.

**Data folder:** `%ProgramData%` lets every user create files, so at start the service (and `Install-Collector.ps1`) gives `%ProgramData%\SensorDeck` a protected ACL — SYSTEM and Administrators full control, Users read — and takes out of use anything inside that is a link (junction, symlink) or not owned by SYSTEM, Administrators or TrustedInstaller (links are deleted — only the link, never its target; files and folders are renamed to `*.untrusted`, never deleted; a `collector.json` written by a normal user ends up as `collector.json.untrusted`). Entries owned by SYSTEM, Administrators or TrustedInstaller are never touched, and every change is logged as a warning. A folder set aside as `*.untrusted` is locked at every start (its owner can still read it but no longer write to it or change its ACL, and the data folder's ACL no longer propagates into it), and links inside it are removed too, so re-applying the ACL can never reach a hard link's target. `Install-Collector.ps1` first makes Administrators the owner of entries owned by the admin running it (e.g. history from an elevated test run), so they aren't set aside. If the folder itself is a link or owned by someone else, the service doesn't use it: no `collector.json`, no logs, no disk history, and `Persist` reports a `ConfigError`. Run `Install-Collector.ps1` again (it makes Administrators the owner) and restart the service; don't delete the folder, it holds the history.

### Ordering guarantees

1. `Hello` is first, followed by `SourceStatus` and `Catalog`.
2. A `Values` frame is only sent after the `Catalog` with the same version. **Clients must ignore `Values` whose `CatalogVersion` doesn't match their current catalog.**
3. Control messages (Hello, Catalog, SourceStatus, HistoryResponse) are never dropped. If a client reads slowly, older `Values` frames are dropped and only the newest is sent.

### Sensor identity

- `SensorInfo.Id` is stable across restarts on the same hardware, prefixed by source: `lhm:/lpc/nct6687d/0/fan/1`.
- Layouts, renames and thresholds in the UI key on this id.
- `NominalValue` is set for recognized voltage rails (+12V, +5V, +3.3V, standby rails, CMOS battery).

### Units

Values arrive in canonical units per `SensorKind`: V, A, W, MHz, °C, %, RPM, L/h, GB, MB, B/s, s, mWh, dBA. `Timing` (memory SPD) has no fixed unit. Conversion (e.g. °F) is the client's job; see `UnitFormatter`.

## Collector settings

Optional file `%ProgramData%\SensorDeck\collector.json`:

```json
{
  "Collector": {
    "PollIntervalMs": 1000,
    "HistorySeconds": 3600,
    "StorageEnabled": false,
    "NetworkEnabled": false,
    "ControllersEnabled": false,
    "DiskHistoryEnabled": true,
    "DiskRawHours": 24,
    "DiskMinuteDays": 7,
    "DiskHistoryMaxMB": 1024
  },
  "Logging": {
    "LogLevel": { "Default": "Information" }
  }
}
```

- `ControllersEnabled` stays **false** on machines where FanControl (or its plugins) owns USB fan/AIO controllers.
- The app's Settings page edits `PollIntervalMs` and the three sensor groups through `SetConfigRequest(Persist: true)`; the collector rewrites the file atomically.
- A file the collector can't read or bind (a syntax error, a duplicate key, `"PollIntervalMs": "fast"`) is ignored: the service starts on its defaults and logs the error. (A bad `Logging:LogLevel` value still stops it.)
- Logs: `%ProgramData%\SensorDeck\logs\collector-yyyyMMdd.log`, kept 7 days, at most 20 MB per day and 100 MB in total.

### Command line

| Switch | Setting | Notes |
|---|---|---|
| `--pipe <name>` | `Collector:PipeName` | Serve on another pipe (a second collector side by side). |
| `--replay <file>` | `Collector:ReplayFile` (command line only) | Demo/replay mode: play back an `.sdrec` recording or a hardware report (looping) instead of reading hardware. No admin rights needed; disk history is off, `Persist` is ignored (status reports a `ConfigError`), and logs go to `%TEMP%\SensorDeck-replay\<app process id>\logs` (one folder per replay window). |
| `--parent-pid <pid>` | `Collector:ParentProcessId` (command line only) | Exit when that process exits (the app starts replay collectors this way). |

The command line overrides `collector.json`. `ReplayFile` and `ParentProcessId` are read from the command line only;
the same keys in `collector.json` are ignored, so the file can't put the service into replay mode. The app's Diagnostics → *Open replay…* starts
`SensorDeck.exe --replay <file>`, which runs its own collector with `--replay <file> --pipe SensorDeck-replay-<pid> --parent-pid <pid>`
and keeps its settings in `%AppData%\SensorDeck\replay-profiles\<file name>`.

## Recordings (`.sdrec`)

Gzip-compressed UTF-8 JSON lines (`SensorDeck.Core.Replay.SensorRecording`):

```text
{"format":"sensordeck-recording","version":1,"createdUnixMs":…,"intervalMs":1000,"description":"…"}
{"catalog":{"version":3,"hardware":[…],"sensors":[…]}}      ← whenever the sensor list changes
{"t":0,"v":[45.5,null,1.208,…]}                             ← one line per poll, ms since the first, catalog order
```

Enums are written as names. Non-finite values are `null`. Readers ignore unknown properties; a `version` above theirs
is refused with "made by a newer SensorDeck". Loading is bounded (the file may come from anywhere and gzip expands):
at most 200,000 frames, 1,000 catalogs, 20,000 sensors per catalog and 32 million values in all (longer recordings are
cut there), lines up to 16 M characters, and every sensor must have an id. Hardware reports (the app's *Save hardware report* and `sensordeck-cli
json`) load as a one-frame recording; replay adds a little movement so charts look alive.

## Trying it

```powershell
# Run the collector in the foreground (elevated terminal for full sensors):
dotnet run --project src/SensorDeck.Collector

# In another (normal) terminal:
dotnet run --project src/SensorDeck.Cli -- connect
dotnet run --project src/SensorDeck.Cli -- connect --kind fan,control
```
