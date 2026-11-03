# SpaceDeck privacy note

**Short version: SpaceDeck has no telemetry, no accounts and no ads. Your sensor data stays on your PC unless you
turn on one of the optional features below, and each of those only talks to the place you chose.**

## What stays on your PC

- Sensor readings, history and recordings: `%ProgramData%\SpaceDeck` (collector: settings, logs, history) and
  `%AppData%\SpaceDeck` (app: settings, dashboards, alert rules, sensor names, recordings, replay profiles, logs).
- Hardware reports, diagnostics text, recordings (`.sdrec`) and backups (`.zip`) are only created when you ask, and
  saved where you choose. Nothing is uploaded. If you send one to someone, it contains sensor names and readings and
  your hardware model names (a backup also contains your PC's name); it contains no personal files.
- Backups leave out paired network-dashboard devices, the pairing PIN and the ntfy access token.

## Optional features that use the network (all off by default)

| Feature | Where it connects | What is sent |
|---|---|---|
| **Network dashboard** (Settings) | Only browsers on your own home network (private addresses); requests from the internet are refused | Your dashboards and live readings, to devices you paired with the PIN |
| **Phone notifications via ntfy** (Settings) | The ntfy server you set (ntfy.sh by default) | The alert's title and text (sensor name, value, limit) to your topic. On the public server anyone who knows the topic name can read it, so the default topic is long and random |
| **Update check** (About) | The release page (GitHub) | A plain request for the latest version, with "SpaceDeck/version" as the user agent; nothing about you or your PC |
| **Download and install** (About, only when you click it) | GitHub release downloads | Plain requests for the installer and its checksum file, with the same user agent; nothing about you or your PC |

## Local programs SpaceDeck talks to

- **The SpaceDeck collector** (a Windows service on the same PC), through a local named pipe.
- **FanControl**, if installed: SpaceDeck only asks it to load one of your saved configs.

## Safety

SpaceDeck shows what the sensors report and can warn you about limits, but it is **not a safety device**. Readings
can be wrong or missing, and alerts can be late or not appear. Don't rely on it to protect hardware, people or
property; your hardware's own protections (thermal throttling, shutdown) remain what keeps it safe. The software is
provided "as is", without warranty (see LICENSE).
