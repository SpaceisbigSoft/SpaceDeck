# Changelog

What changed in each SpaceDeck release. The newest version is at the top. Each section is also the text of that
version's [GitHub release](https://github.com/SpaceisbigSoft/SpaceDeck/releases) and what the app shows under
About → Updates.

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
