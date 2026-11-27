# Lian Li GA II LCD — screen frames and the screen ring (register note)

SpaceDeck's code is written from notes like this one, not from other projects' code (native sensors plan, "Licence
rule"). Each fact says where it came from. Device: GA II LCD `0416:7395` (also `0416:7391`), firmware `1.6`, one HID
collection on usage page 0xFF1A (see `lianli-galahad2.md` for the device and the 64-byte command frame).

## What SpaceDeck (through its FanControl plugin) sends

| Command | Report | Why |
|---|---|---|
| 0x81 status, 0x86 firmware | 0x01 | Readings (already in use) |
| 0x8A set pump, source = motherboard | 0x01 | Pump follows the header (already in use) |
| **0x0E SendJpeg** | 0x02 | One 480×480 JPEG frame on the screen |
| **0x83 SetPumpLighting** | 0x01 | The ring of LEDs around the screen: a built-in effect, up to 4 colours |
| **0x14 SetLEDColors, screen path** | 0x02 | The ring, one colour per LED (OpenRGB "Direct") |
| 0x82 GetPumpLightingInfo | 0x01 | Query: the ring's effect list, max speed, max brightness |

**Never sent:** 0x0F UploadJpeg (may write flash; no visible effect on fw 1.6), 0x8E ResetDevice (drops the device off
USB for 1–3 s and the pump with it), 0x0D/H.264 streaming, 0x0C LCDControl test mode.

## B packet (report 0x02, 1024 bytes out)

| Byte | Meaning |
|---|---|
| 0 | Report id 0x02 |
| 1 | Command |
| 2–5 | Total payload size across all packets, big-endian u32, the same on every packet |
| 6–8 | Packet number, big-endian u24, from 0 |
| 9–10 | Payload bytes in this packet, big-endian u16 (≤ 1013) |
| 11–1023 | Payload, zero-padded |

A payload is split into 1013-byte chunks. The cooler answers a B command with one B packet (cmd echo); L-Connect
reads it with a 20 ms timeout and discards it. Status (0x81) replies can arrive in between.

Sources: lian-li-linux `hydroshift_lcd/protocol.rs` (`build_lcd_packet`, B_HEADER_LEN 11, B_MAX_PAYLOAD 1013) and
`controller.rs` (`send_chunked_with`, `read_ack`); ll-control `docs/development/protocol/galahad2-lcdv2.md`
("BCommandPacket", from L-Connect's `BCommandPacket.cs`).

## 0x0E SendJpeg

- Payload = the raw JPEG (starts FF D8, ends FF D9), 480×480. No header, no flag byte.
- Shown within ~50 ms of the last packet. Live-verified on 0x7395 fw 1.6 with 20–56 KB JPEGs (21–56 packets).
- **Not persistent:** the screen goes back to its built-in display ("LocalUI") about 20 s after the last frame,
  whatever the LCD mode. A static image must be re-sent at least every ~15 s. SpaceDeck relies on this as its
  failsafe: when SpaceDeck stops sending, the cooler shows its own screen again.
- No LCDControl (0x0C) "Application" mode is needed first on fw 1.6.
- A write that fails part-way leaves the cooler's transfer state unknown; the next complete frame recovers it.

Sources: ll-control `galahad2-lcdv2.md` §SendJpeg (live captures 2026-04-25, 0x7395 fw 1.6); lian-li-linux
`controller.rs` `send_jpeg` (report 0x02); CaseHUD (kewe87) `internal/panel/hydroshift.go` (same command).

## 0x83 SetPumpLighting (the screen ring's built-in effects)

A-packet (report 0x01), payload length 19:

| Payload byte | Meaning |
|---|---|
| 0 | Scope: 0 inner, 1 outer, 2 all. On the Vision only **outer (1) or all (2) light the ring**; inner is accepted but shows nothing (ll-control live test). L-Connect writes 0 for the Vision's ring (plugin's `Galahad2LightingEncoder`) — to check on the owner's cooler. |
| 1 | Mode (below) |
| 2 | Brightness 0–4 (0 = off) |
| 3 | Speed 0–4 |
| 4–15 | Four colours, R,G,B each |
| 16 | Direction: 0 clockwise, 1 counter-clockwise, 2 up, 3 down, 4 spread, 5 gather |
| 17 | Disabled (1 = ring off) |
| 18 | Signal source: 0 the cooler's MCU (our colours), 1 the motherboard's ARGB header |

Modes (0x82 on 0x7395 fw 1.6 lists 16): 1 Rainbow, 2 Rainbow morph, 3 Static, 4 Breathing, 5 Runway, 6 Meteor,
7 Ticker tape, 8 Fluctuation, 9 Transmit, 10 Colourful starry night, 11 Static starry night, 12 Voice, 13 Big bang,
14 Burst, 15 Colours morph, 16 Bounce. lian-li-linux offers only Off, Static, Rainbow, Rainbow morph, Breathing,
Runway, Meteor for the Vision.

No reply. Several writes in quick succession can leave the ring showing an older one until it is idle for a moment;
L-Connect re-sends 0x83 on a timer. Leave ≥ 1 s between writes and re-send the current look now and then.

Sources: ll-control `galahad2-lcdv2.md` §SetPumpLighting / §GetPumpLightingInfo (live); lian-li-linux
`hydroshift_lcd/rgb.rs` (`set_pump_light`), `lianli-shared/src/rgb.rs` (mode and direction bytes, brightness ≤ 4).

## 0x14 SetLEDColors, screen path (one colour per ring LED)

B packet(s) (report 0x02), command 0x14. Payload: byte 0 = 0x00 (screen path; 0x01 is the fan path), bytes 1–24 =
zero (eight unused colour slots, required), then R,G,B per LED. Length = (LEDs + 8) × 3 + 1. The ring has
**12 LEDs** (lian-li-linux zone "Pump Head", 12). Overrides any 0x83 look until the next 0x83. No reply expected.

Sources: ll-control `galahad2-lcdv2.md` §SetLEDColors (live: ring red / green / yellow), from L-Connect's
`Galahad2VisionDevice.cs` size formula; lian-li-linux `rgb.rs` `zones` (12).

## 0x82 GetPumpLightingInfo

A-packet query, no payload. Reply cmd 0x82: mode bytes until `A5 5A`, then max speed, max brightness (4 and 4 on
fw 1.6). An `A5` followed by anything but `5A` drops that byte.

## Not covered (radiator fans)

0x85 SetFanLighting drives fans wired to the cooler's own fan header. The owner's TL wireless fans are not wired to
it; their lighting goes over the L-Wireless dongle (transmitter 0416:8040, missing since ~19–22 Nov).
