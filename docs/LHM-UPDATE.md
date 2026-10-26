# Updating LibreHardwareMonitorLib

SensorDeck reads hardware through LibreHardwareMonitorLib (LHM). New LHM versions add boards and chips, but they can
also rename sensors, change sensor ids, or change how a voltage is scaled. Saved dashboards, alert rules, renames and
scales are keyed on sensor ids, so an update needs a quick before/after check.

## Routine

1. **Before:** with the current build, save a hardware report under a steady, idle load.
   - App: Diagnostics → *Save hardware report…* → `before.json`, or
   - CLI (elevated): `sensordeck-cli json > before.json`
2. **Update the package** in `Directory.Packages.props` (`LibreHardwareMonitorLib`) and read LHM's release notes
   for breaking changes (renamed sensor types, removed hardware, PawnIO changes).
3. **Build and test:** `dotnet build -c Release` then `dotnet test`. Fix any API changes in
   `src/SensorDeck.Sources.Lhm` (`LhmSource`, `LhmMapping`). New LHM `SensorType`s must be mapped in `LhmMapping`;
   enum values in `SensorKind` are wire format, so only ever append.
4. **After:** reinstall/restart the collector with the new build (`scripts\Install-Collector.ps1`), wait a minute
   at the same idle load, and save `after.json` the same way.
5. **Compare:** `sensordeck-cli diff before.json after.json`
   - *Sensors removed / kind changed* (exit code 3): ids changed. Dashboards and alert rules pointing at them show
     as missing; open the dashboard and use *Relink* (or the missing-sensor prompt) to point them at the new ids.
     If many ids moved the same way, consider a mapping in `SensorLinker` so users don't relink by hand.
   - *Renamed*: harmless for layouts (ids unchanged); check `DefaultDashboard` / `DefaultAlertRules` still find
     their sensors by name (they try several names per tile).
   - *Readings moved a lot*: usually a voltage scaling change. Compare against BIOS/HWiNFO; fix or document the
     per-sensor scale in the app's sensor editor.
   - *Nominal voltage changed*: rail tolerance bars and default voltage alerts use the nominal; check
     `VoltageRails` name rules.
6. **Replay check:** open `after.json` with Diagnostics → *Open replay…* and confirm the default dashboard still
   looks right (a fresh replay profile builds one from scratch). Do the same with the sample in
   `samples/amd-laptop-report.json` to cover AMD, laptop and two-GPU fallbacks.
7. **Update the About page / THIRD-PARTY-NOTICES** if LHM's license or bundled dependencies changed (HidSharp etc.).

## Notes

- Hardware reports and `.sdrec` recordings both work with `diff` and replay.
- Keep `before.json`/`after.json` with the PR that bumps LHM; they document what changed for users.
- Never bundle or modify PawnIO; if a new LHM needs a newer PawnIO, say so in the release notes and the
  Diagnostics check text.
