# Plugin pipe — readings from FanControl plugins to the collector

The SpaceDeck collector takes sensor readings from SpaceDeck's own FanControl plugin (the Lian Li plugin,
`SpaceisbigSoft/FanControl.LianLi`) over a second named pipe. Readings only: nothing on this pipe is ever a command,
and the collector never acts on what it receives except to show it. SpaceDeck's app and collector stay read-only for
fans and pumps; FanControl, through the plugin, does all fan control.

## Transport

- Pipe `\\.\pipe\SpaceDeck.Plugins`, hosted by the collector (the `SpaceDeckCollector` service, LocalSystem). The
  plugin is the client and may run in FanControl.exe (the user's session) or FanControl.Service.exe (LocalSystem).
- ACL as the app pipe (`PipeServer`): Authenticated Users read/write; SYSTEM, Administrators and the collector's own
  account full control. First instance created with `FirstPipeInstance`; at most 2 instances (FanControl's app and
  service can overlap briefly while control moves between them).
- **The collector checks the client** before reading anything: `GetNamedPipeClientProcessId`, then the process image
  must be `FanControl.exe` or `FanControl.Service.exe` in a folder that passes the same "no untrusted writer" rule as
  FanControl auto-detection (`FanControlLocator`). Anything else is disconnected and logged once.
- **The plugin checks the server**: it connects with the lowest impersonation level (identification) and checks with
  `GetNamedPipeServerProcessId` that the server is the `SpaceDeckCollector` service process.
- The plugin connects from its own background thread, never from FanControl's calls; connect timeout 250 ms, backoff
  1 → 30 s while the collector is absent.

## Framing

UTF-8 JSON, one object per line (`\n`). No BOM. A line is at most 64 KiB; a longer one disconnects the client.

Every object has `"t"` (its type). Unknown types are ignored; unknown fields are ignored.

### `hello` — first line after connecting

```json
{"t":"hello","v":1,"plugin":"Lian Li Uni (Lighting)","version":"2.0.7","instance":"9f1c…"}
```

| Field | Meaning |
|---|---|
| `v` | Pipe protocol version. This document is version 1. A collector that doesn't know the version disconnects. |
| `plugin` | The plugin's FanControl Name. |
| `version` | The plugin build. |
| `instance` | A new id each time the plugin object is created (FanControl recreates plugins on refresh). |

### `catalog` — after `hello`, and again whenever the set of sensors changes

```json
{"t":"catalog",
 "hardware":[{"id":"LianLi/0","name":"Lian Li Galahad II LCD","kind":"cooler"},
             {"id":"LianLi/wa1b2c3d4e5f6","name":"Lian Li TL Wireless (3 fans)","kind":"fans"}],
 "sensors":[{"id":"LianLi/0/coolant/temp","hardware":"LianLi/0","name":"Coolant","kind":"temperature"},
            {"id":"LianLi/0/ch1/fan","hardware":"LianLi/0","name":"Pump","kind":"fan"},
            {"id":"LianLi/wa1b2c3d4e5f6/f0/fan","hardware":"LianLi/wa1b2c3d4e5f6","name":"Fan 1","kind":"fan"}]}
```

- `id`: the plugin's own sensor id (FanControl's, without the plugin Name), 1–128 characters from
  `A–Z a–z 0–9 / . _ : -`. Stable across restarts; the collector publishes it as `lianli:/<id>`.
- `name`: 1–64 printable characters.
- Hardware `kind`: `cooler` or `fans` (anything else → Other).
- Sensor `kind`: `temperature` (°C), `fan` (rpm) or `control` (%, the duty FanControl is applying).
- At most 32 hardware entries and 512 sensors. Every sensor's `hardware` must name a listed hardware id; duplicates
  are an error. An invalid catalog disconnects the client.

### `values` — about once a second

```json
{"t":"values","values":[21.6,2910,1180]}
```

- One entry per sensor, in catalog order; `null` = not measured right now.
- Numbers must be finite and in range for the kind: temperature −50…150, fan 0…20000, control 0…100. An
  out-of-range number is stored as `null` (not a disconnect: a cooler's odd reading shouldn't drop every sensor).
- A count that doesn't match the catalog disconnects the client.
- More than 5 lines a second disconnects the client.

## Collector behaviour

- Source name `lianli` (`SourceStatus` and Diagnostics show it). Before any plugin has connected: `DeviceNotFound`,
  "FanControl's Lian Li plugin isn't connected".
- Values go blank 3 s after the last `values` line, and on disconnect. The catalog is kept for the collector's
  lifetime (sensors that are gone read blank), so dashboards stay laid out across plugin restarts; a new catalog
  adds sensors.
- One client feeds the source at a time: a new connection replaces the old one (the newest is the live FanControl).
- Read timeout 30 s with no line at all disconnects (the plugin sends `values` every second).
- Nothing received is ever used for anything but display, history and alerts.
