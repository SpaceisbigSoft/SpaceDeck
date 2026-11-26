# Lian Li Galahad II — status reply (register note)

SpaceDeck's readers are written from notes like this one, not from other projects' code (see the native sensors
plan, "Licence rule"). Each fact says where it came from.

## Device

| Product | USB id | Notes |
|---|---|---|
| Galahad II LCD | 0416:7391 | Firmware codename "Vision" (lewisgibson/FanControl.LianLi calls it "Galahad II Vision") |
| Galahad II LCD (second id) | 0416:7395 | Same family; liquidctl reads firmware string `CA_II-Vision` from it |
| Galahad II Trinity Performance / Trinity | 0416:7371 / 0416:7373 | Same status command, but no coolant fields |

The command channel is a vendor-defined HID collection (usage page 0xFF1B according to lewisgibson's plugin;
to confirm per id with `spacedeck-cli lianli-listen`).

## Frame (64-byte HID report)

| Byte | Meaning |
|---|---|
| 0 | Report id, 0x01 |
| 1 | Command (0x81 = status / handshake) |
| 2 | 0 |
| 3–4 | Packet number |
| 5 | Payload length |
| 6… | Payload |

Request: `01 81 00 00 00 00` followed by zeros. A query: no implementation sends settings with it. The cooler never
sends a report on its own; it only answers.

## Status reply payload

| Payload byte (packet byte) | Meaning |
|---|---|
| 0–1 (6–7) | Radiator fan RPM, big-endian |
| 2–3 (8–9) | Pump RPM, big-endian |
| 4 (10) | Coolant temperature valid, non-zero = valid |
| 5 (11) | Coolant temperature, whole °C |
| 6 (12) | Coolant temperature, tenths (0–9) |

Bytes 4–6 exist only when the payload length is 7. On the Trinity, packet byte 11 is not a temperature.

Example (real capture): `01 81 00 00 00 07 05 a0 0a 6e 01 25 08` = fan 1440 rpm, pump 2670 rpm, valid, 37.8 °C.

## Sources

- liquidctl, `liquidctl/driver/ga2_lcd.py` (handshake decode, requires length 7) and `tests/test_ga2_lcd.py` (the
  capture above, from a 0x7395 unit). MIT. https://github.com/liquidctl/liquidctl
- sgtaziz/lian-li-linux, `crates/lianli-devices/src/hydroshift_lcd/` (same layout, valid flag, tenths modulo 10;
  lists 0x7391 and 0x7395). MIT. https://github.com/sgtaziz/lian-li-linux
- chdao/FanControl.GalahadII (coolant from packet byte 11, 0x7395 only). MIT.
- lewisgibson/FanControl.LianLi `src/FanControl.LianLi/Protocol/Galahad2Protocol.cs` (decodes fan and pump only;
  Trinity note in its issue #30). MIT.

## Still to confirm on the dev PC (0x7391)

Payload length 7 and a plausible byte 11 on this id; the collection's usage page and report length; whether a
second read-only handle receives the replies the FanControl plugin's polling triggers. Run:
`spacedeck-cli lianli-listen --seconds 30 --out <file>`.
