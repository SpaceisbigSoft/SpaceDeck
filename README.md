# SensorDeck

A native Windows hardware-monitoring dashboard: voltages, temperatures, fans, clocks, loads and power in a polished, customizable UI.
Reads hardware through **LibreHardwareMonitorLib** on the **PawnIO** driver, with no HWiNFO needed.

> Status: early development. Sensors, collector service and FanControl profile switching work on Windows. The WPF app, dashboards, alerts, automation and the network dashboard are written; see [docs/TEST-PLAN.md](docs/TEST-PLAN.md) for what is verified.
> See [docs/HANDOFF.md](docs/HANDOFF.md) for current status, the checklist, the full plan and the changelog.

## Requirements

- Windows 10 (2004+) or 11, on real hardware (VMs expose few sensors)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2026, or 2022 17.14+, with the ".NET desktop development" workload (needed for the `.slnx` solution format and WPF)
- [PawnIO](https://pawnio.eu), official signed edition

## Build and test

```powershell
dotnet build SensorDeck.slnx
dotnet test SensorDeck.slnx
```

Or open `SensorDeck.slnx` in Visual Studio.

## Check your sensors

Run from an **elevated** terminal (right-click Terminal → Run as administrator):

```powershell
dotnet run --project src/SensorDeck.Cli -- list
dotnet run --project src/SensorDeck.Cli -- watch --kind voltage,temperature,fan
dotnet run --project src/SensorDeck.Cli -- json > sensordeck-dump.json
```

The first lines show whether PawnIO was found and whether you're running as administrator.

## Collector service

The collector reads sensors in the background and serves them to the dashboard over `\\.\pipe\SensorDeck` (see [docs/PROTOCOL.md](docs/PROTOCOL.md)).

```powershell
# Try it in the foreground (elevated terminal):
dotnet run --project src/SensorDeck.Collector
# Watch its live data from a normal terminal:
dotnet run --project src/SensorDeck.Cli -- connect

# Install / update as a Windows Service (elevated):
.\scripts\Install-Collector.ps1
# Remove it:
.\scripts\Uninstall-Collector.ps1
```

## The app

```powershell
# With the collector running (service or foreground):
dotnet run --project src/SensorDeck.App
```

## Fan profiles (FanControl)

SensorDeck never controls fans itself. It asks [FanControl](https://github.com/Rem0o/FanControl.Releases) to load a saved config, so the two never fight. Save one config per profile in FanControl (File → Save as…), then:

```powershell
dotnet run --project src/SensorDeck.Cli -- fancontrol                 # find FanControl, list configs
dotnet run --project src/SensorDeck.Cli -- fancontrol switch Quiet    # load Quiet.json
```

## Layout

| Path | What |
|---|---|
| `src/SensorDeck.Core` | Shared model, `ISensorSource`, pipe protocol, unit formatting, voltage-rail logic |
| `src/SensorDeck.Sources.Lhm` | LibreHardwareMonitor source, PawnIO and elevation checks |
| `src/SensorDeck.Integrations.FanControl` | Finds FanControl, lists its configs, switches config |
| `src/SensorDeck.Cli` | `sensordeck-cli` diagnostics tool |
| `src/SensorDeck.Collector` | Windows Service that polls sensors and serves the UI over a named pipe |
| `src/SensorDeck.App` | WPF app: dashboards, sensor tables, voltages, fans, history, alerts, automation, settings, network dashboard, FanControl profile picker |
| `tests/SensorDeck.Core.Tests` | xUnit tests for Core |
| `tests/SensorDeck.Collector.Tests` | History store + end-to-end pipe tests with a fake source |
| `scripts/` | Install/uninstall the collector service |
| `docs/` | Plan, handoff, pipe protocol, test plan, LHM update routine |

## License, privacy and safety

- SensorDeck is licensed under the [MIT License](LICENSE) (Copyright (c) 2026 SensorDeck contributors).
- Third-party components and their licenses: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). LibreHardwareMonitorLib
  and its helper libraries are MPL-2.0, HidSharp is Apache-2.0, the rest are MIT.
- PawnIO and FanControl are installed separately by the user and are never bundled or modified; SensorDeck is not
  affiliated with either.
- No telemetry. Network features are opt-in; see [PRIVACY.md](PRIVACY.md).
- **Not a safety device:** readings can be wrong and alerts can be late. Don't rely on SensorDeck to protect
  hardware, people or property.
