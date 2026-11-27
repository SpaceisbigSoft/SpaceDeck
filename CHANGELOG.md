# Changelog

What changed in each SpaceDeck release. The newest version is at the top. Each section is also the text of that
version's [GitHub release](https://github.com/SpaceisbigSoft/SpaceDeck/releases) and what the app shows under
About → Updates.

## [0.2.0] - 2026-11-27

### Added
- **Cooler screen** for the Lian Li GA II LCD (Galahad II Vision): show a dashboard, a picture, an animated GIF, a
  video or a slideshow of a folder on the cooler's own 480×480 screen, smooth at up to 30 frames a second. Settings →
  Cooler screen: content, fit or fill, background colour, rotation (for a cooler mounted upside down), picture quality
  and frame rate. Moving pictures are kept small enough for the cooler to show them without flicker.
- **Screen-shaped dashboards:** a square editor with a live preview, a built-in "Cooler screen" layout with large
  numbers ("Reset to default layout…"), a new **Image** tile for a picture or GIF, and a picture or GIF as any
  dashboard's background.
- **Lian Li cooler readings** (coolant temperature, pump and fan speed) in SpaceDeck, from FanControl.
- **Screen ring lighting through OpenRGB:** OpenRGB can set the GA II LCD's ring (Direct, Off, Static, Breathing,
  Rainbow, Rainbow Morph, Runway, Meteor). "Save to device" keeps the look across restarts, and the **L-Connect** mode
  gives the ring back to the look saved in L-Connect. In OpenRGB: Settings → SDK Client, add `127.0.0.1` port `6743`.
- Diagnostics shows the cooler screen's frames a second and which program is sending them.

**The cooler screen, the cooler readings and the ring need SpaceDeck's FanControl plugin**, attached to this release as
`FanControl.LianLi-SpaceDeck-<version>.zip` (the MSI doesn't install it; FanControl loads its own plugins). Unzip it and
add `FanControl.LianLi-SpaceDeck.dll` with FanControl's **Install plugin** button. If you used the upstream Lian Li
plugin before, this one takes over its devices; check that your fan curves still point at the Lian Li fans and pump.

### Changed
- Fan and pump sparklines no longer draw a small speed wobble (a pump moving between 3,000 and 3,060 rpm) as drops
  to zero.

## [0.1.2] - 2026-11-03

### Changed
- New app icon: an orange gauge on a dark tile, for the program, tray, taskbar, Start menu and Settings → Apps. The
  tray uses a version drawn for small sizes, so it stays sharp.

## [0.1.1] - 2026-11-03

### Fixed
- Motherboard sensors (voltages, fans and temperatures from the board's Super I/O chip) could stay missing after the
  collector started, when FanControl was reading the board at the same moment. They now appear within a few seconds.
- Uninstalling, upgrading or repairing while SpaceDeck was open left it running without its files. The installer now
  closes SpaceDeck first.

## [0.1.0] - 2026-11-03

First public release.

### Added
- **Dashboards you design:** a 12-column grid of tiles (big numbers, gauges, bars, sparklines, charts, voltage rails,
  fans, core maps, network and disk throughput, clocks), as tabs, in their own windows or as overlays.
- **Screen-edge sidebar** that fits itself to the screen height and hides during fullscreen games.
- **All sensors** in one searchable table; rename, hide or correct sensors, and add calculated sensors.
- **Alerts** with sensible default limits, Windows notifications, sounds, quiet hours and optional phone alerts (ntfy).
- **History:** 24 hours at full rate and 7 days per minute, kept by the collector even while the app is closed; CSV
  export and recording sessions.
- **FanControl profile switching** by hand (window, tray, phone) or with automation rules.
- **Phone and tablet dashboard** in a browser on your home network, paired with a PIN.
- 16 skins, °C/°F, 12/24-hour clock, backup and restore.
- **Installer** with everything included (no separate .NET install), and **auto-update** from About → Updates.
