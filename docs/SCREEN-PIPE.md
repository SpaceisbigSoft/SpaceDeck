# Screen pipe — cooler screen frames from the collector to the FanControl plugin

SpaceDeck can show one of its dashboards on a cooler's own screen (first: the Lian Li GA II LCD, 480×480). The app
draws the frame, the collector passes it on, and SpaceDeck's FanControl plugin
(`SpaceisbigSoft/SpaceDeck.FanControl.LianLi`, branch `spacedeck`) sends it to the cooler, because the plugin is the
only program that opens the cooler (one owner, one lock for status, pump and screen).

```
SpaceDeck.App ──app pipe (ScreenFrame)──► Collector ──\\.\pipe\SpaceDeck.Screen──► FanControl plugin ──HID 0x0E──► cooler
   draws 480×480 JPEG                     one sender per screen,                    validates, sends on the
   every 1–5 s (up to 30 a second)        keeps the latest, forwards new frames     cooler's worker thread
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
- **One sender per screen.** The app-pipe client whose frame was last accepted for a screen owns it; frames for that
  screen from any other client are refused (counted apart, as "refused (another sender)", not as dropped) until the
  owner has sent nothing for 3 s or disconnects, and then the next sender takes over. So the app's dashboard and a
  `screen-test` never interleave on one screen (the one that was there first keeps it).
- At most 30 frames a second per hardware id on average, as a token bucket: 6 tokens, refilled at 30 a second. A frame
  with a token passes at once, so a stream of up to 30 a second passes whole however its frames jitter (a timer tick
  early or late); a frame without one is dropped (counted in Diagnostics), not queued. 30 is enough for smooth motion
  and what the plugin sends to a cooler at most.
- The collector writes to the screen pipe at most 30 messages a second in all on average, with the same kind of bucket
  (6, refilled at 30 a second), and never sleeps to pace: a frame is written the moment it arrives while a token is
  there. Without a token the frame waits in its slot (newest only) and the writer wakes when the next token is due;
  the system timer may round that wake up (15.6 ms on Windows), but no frame of a stream of up to 30 a second is lost
  to it. (A fixed 33.3 ms gap slept on that timer is 46.8 ms: 21 frames a second.)
- Diagnostics shows the pipe's status line, e.g. `Screen pipe connected · 1200 frames sent · 4 dropped · 29.8 frames/s
  · 40 refused (another sender) · from SpaceDeck`: frames written to the plugin and dropped since the collector started,
  frames a second written over the last 5 s, then (when there are any) frames refused because another client held the
  screen and the programs sending now. `spacedeck-cli screen-test --hardware <id> --fps <1–30> --quality <30–100>` reads
  it back to measure what the path carries, and warns once when its frames are refused because another sender (the
  app) holds the screen.
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
out of range, a payload that doesn't start `FF D8` and end `FF D9`, or more than 30 messages a second on average
(token bucket: 60, refilled at 30 a second, so a collector catching up after a stall isn't taken for a flood). An id
it has no screen for is ignored (logged once).

### What the plugin does with a frame

- Keeps only the newest frame per cooler (a slot, replaced, never queued) and drops a frame it has held for more than
  5 s without sending.
- Sends it on that cooler's worker thread, between the status polls, as 0x0E in 1013-byte B packets, at most
  30 frames a second per cooler on average and at most one per worker tick: a token bucket of 3, refilled at 30 a
  second, so jittered frames at up to 30 a second are all sent at once; a frame without a token waits for a later
  tick, replaced by any newer one. The status poll
  stays at most every 500 ms however often frames wake the worker. A failed write costs the frame and is logged; fan
  and pump control never wait on a screen frame for more than one transfer.
- Once a minute while frames are sent, `plugin.log` has one line per cooler: frames sent, frames dropped before
  sending (replaced or expired), the average and longest time to write one frame, and the average frame size.
- Never repeats a frame, never sends anything to the screen without a frame, and sends nothing to the screen when
  no SpaceDeck collector is installed.
