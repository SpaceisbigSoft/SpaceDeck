# Screen pipe — cooler screen frames from the collector to the FanControl plugin

SpaceDeck can show one of its dashboards on a cooler's own screen (first: the Lian Li GA II LCD, 480×480). The app
draws the frame, the collector passes it on, and SpaceDeck's FanControl plugin
(`SpaceisbigSoft/SpaceDeck.FanControl.LianLi`, branch `spacedeck`) sends it to the cooler, because the plugin is the
only program that opens the cooler (one owner, one lock for status, pump and screen).

```
SpaceDeck.App ──app pipe (ScreenFrame)──► Collector ──\\.\pipe\SpaceDeck.Screen──► FanControl plugin ──HID 0x0E──► cooler
   draws 480×480 JPEG                     keeps the latest,                         validates, sends on the
   every 1–5 s                            forwards new frames                       cooler's worker thread
```

**What a frame can do:** show a picture on the cooler's screen. Nothing on this pipe is a fan, pump or lighting
command, and the plugin builds every USB packet itself; the only bytes from the pipe that reach the cooler are a
JPEG's, as the payload of 0x0E SendJpeg (`docs/registers/lianli-galahad2-screen-and-ring.md`).

**Failsafe:** the cooler drops a frame and shows its built-in screen about 20 s after the last one. Every stage only
ever passes *new* frames on and never repeats an old one, so when the app stops drawing (closed, crashed, the screen
turned off in Settings), the cooler shows its own screen again within ~25 s. Nothing has to be undone.

## 1. Which coolers have a screen — plugin pipe catalog

The plugin's `catalog` line (`docs/PLUGIN-PIPE.md`, version 1) gets an optional field on a hardware entry:

```json
{"id":"LianLi/0","name":"Lian Li GA II LCD","kind":"cooler","screen":{"width":480,"height":480,"format":"jpeg"}}
```

- `width`, `height`: 1–1024. `format`: `"jpeg"` (the only one). Anything else → the entry has no screen (not a
  disconnect; older collectors ignore the field, as unknown fields are ignored).
- The plugin lists it only for a cooler it can drive a screen on (product 0x7391, 0x7395).

The collector shows it to the app as part of the hardware info (hardware id `lianli:/LianLi/0`, screen size), so
Settings can list "Cooler screens".

## 2. App → collector

A new app-pipe request `ScreenFrame { HardwareId, Jpeg }` (protocol version bump, `docs/PROTOCOL.md`):

- `HardwareId`: a hardware id the collector has from the plugin with a `screen` (e.g. `lianli:/LianLi/0`).
- `Jpeg`: 1 byte – 256 KiB, starts `FF D8`, ends `FF D9`, the screen's size (the collector checks the markers and
  the size field only; it does not decode).
- At most 2 frames a second per hardware id; more are dropped (counted in Diagnostics), not queued.
- The collector keeps only the newest frame per screen and forwards each frame once. It never forwards a frame older
  than 5 s (so a frame that waited for the plugin to reconnect is dropped, not shown late).
- Who may send: the same clients that may use the app pipe today (Authenticated Users). A frame can only change the
  picture; see "Admin-only collector settings" in the plan for tightening later.

## 3. Collector → plugin: `\\.\pipe\SpaceDeck.Screen`

### Transport

- Hosted by the collector (the `SpaceDeckCollector` service). ACL and instance rules as `SpaceDeck.Plugins`
  (Authenticated Users read/write, `FirstPipeInstance`, at most 2 instances).
- The plugin connects from its own background thread (as `SpaceDeckPublisher` does: 250 ms connect timeout, backoff
  1 → 30 s while the collector is away), **read-only** at the identification impersonation level, and checks that
  the server is the `SpaceDeckCollector` service process before reading anything.
- The collector checks the client exactly as on the plugin pipe (`FanControl.exe` / `FanControl.Service.exe` in a
  folder no untrusted account can write to); anything else is disconnected and logged once.
- One client at a time: a new connection replaces the old one (the newest is the live FanControl).

### Framing (binary, collector → plugin only)

All integers little-endian.

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Magic `53 44 53 46` ("SDSF") |
| 4 | 1 | Version, 1 |
| 5 | 1 | Kind: 1 = JPEG frame |
| 6 | 2 | Id length N, 1–128 |
| 8 | N | Plugin hardware id, ASCII, the plugin's own id (`LianLi/0`, without `lianli:/`), same character rule as catalog ids |
| 8+N | 4 | Payload length L, 1 – 262144 |
| 12+N | L | JPEG bytes |

The plugin disconnects (and logs once per run) on: wrong magic or version, unknown kind, an id or payload length
out of range, a payload that doesn't start `FF D8` and end `FF D9`, or more than 4 messages a second on average
(token bucket: 8, refilled at 4 a second). An id it has no screen for is ignored (logged once).

### What the plugin does with a frame

- Keeps only the newest frame per cooler (a slot, replaced, never queued) and drops a frame it has held for more than
  5 s without sending.
- Sends it on that cooler's worker thread, between the status polls, as 0x0E in 1013-byte B packets, at most
  2 frames a second per cooler. A failed write costs the frame and is logged; fan and pump control never wait on a
  screen frame for more than one transfer.
- Never repeats a frame, never sends anything to the screen without a frame, and sends nothing to the screen when
  no SpaceDeck collector is installed.
