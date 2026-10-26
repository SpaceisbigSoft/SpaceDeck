# SensorDeck — Phase 12 test plan

Work through the sections in order; tick each box or note what happened. Use the latest Release build
(`dotnet build SensorDeck.slnx -c Release`) and the installed collector service (`scripts\Install-Collector.ps1`).
"Expected" is what should happen; anything else is a bug to report (a screenshot helps).

## A. Automated tests
- [x] `dotnet test` — all pass (202/202, 2026-10-23).

## B. Fresh profile (first run)
1. Exit SensorDeck. Rename `%AppData%\SensorDeck` to `SensorDeck.bak`.
2. Start SensorDeck.
- [x] Opens on Diagnostics with the welcome box; checks are green/notes except things genuinely missing.
- [x] "Looks good" → a default dashboard built from your hardware; default alert rules on the Alerts page.
3. Exit, delete the new `%AppData%\SensorDeck`, rename `SensorDeck.bak` back. Start again: everything as before.

## C. Collector robustness (app keeps running throughout)
- [x] Restart the service (elevated: `Restart-Service SensorDeckCollector`): banner "Connecting…", then live again within ~5 s; history charts continue.
- [x] Stop the service: banner "collector service is stopped" with **Start service**; clicking it (UAC) brings data back.
- [x] Sleep the PC for a few minutes and wake it: values resume, no error dialogs, no stuck "Connecting…".
- [x] Reboot: service starts by itself; app (if "start with Windows" is on) starts in the tray and is live.

## D. Every page, every skin
For each of the 16 skins (Settings → Display), visit every page once. Skins: Monochrome, Monochrome dark, Fluent (light and dark),
Dark HUD, Nord, Dracula, Gruvbox, Cyberpunk, Synthwave, Red Team, OLED black, Paper, High contrast, Amber CRT, Frosted glass.
- [ ] Dashboard (view + edit mode: add, move, resize, delete a tile; undo by deleting the test tile)
- [ ] All sensors (search, rename a sensor, hide/unhide, scale)
- [ ] Voltages, Fans & pump, History (1 h / 24 h / 7 d ranges, CSV export), Recordings, Alerts, Automation, Settings, Diagnostics, About
- [ ] No text cut off or unreadable (contrast) in any skin; accent colour change applies.
- [ ] Each skin also on the sidebar and a dashboard window (section tiles, fan bars, P/E-core gap).
- [ ] High contrast: bigger text still fits the section tiles and sidebar; nothing overlaps.
- [ ] Frosted glass: sidebar and dashboard windows show the wallpaper blurred through them; main window stays solid;
      switching to another skin makes them solid again with no restart; sidebar/dashboard opacity setting still works.

## E. Display and windows
- [ ] UI size 80 % / 125 % / 150 %: main window, dialogs, dashboard windows and sidebar all scale; nothing clipped.
- [ ] Dashboard window on the second monitor; exit and restart → reopens in the same place.
- [ ] Monitors with different Windows scaling (e.g. 100 % and 150 %): dashboard window and sidebar sharp and correctly sized on both; drag a dashboard window across.
- [ ] Sidebar: left/right, both monitors, reserve on/off, fullscreen game hides it, exiting gives the space back.
- [ ] Click-through overlay + Ctrl+Shift+Alt+C; tray toggle.
- [ ] Content size / fit to height: build a sidebar with every CPU core, GPU, RAM and fans so it runs past the screen bottom. With Size → Fit to height (default) everything fits after a moment, slightly smaller, still full width with values right-aligned at the edge; no scrollbar. Hide/show rows (right-click a row), edit sections, change skin, UI size (Settings → Display), monitor, taskbar position/size and Reserve screen space → it re-fits each time and never flickers back and forth. Add so many rows that 70 % isn't enough → stays at 70 %, scrolls, and shows “Doesn't fit — hide some rows”. Size → Smaller/Larger/100 % turns fit off and steps 5 % (70–150 %); Settings → Sidebar → Size shows the same choice; restart → remembered. Dashboard window: Size menu works the same, off (100 %) by default, remembered per dashboard. Main window and LAN web dashboard unaffected.

## F. Alerts, automation, notifications
- [ ] Temporarily set a low limit (e.g. CPU warning 30 °C): notification + sound; alerts.log entry; clears when raised back.
- [ ] Quiet hours suppress it; ntfy (if used) reaches the phone.
- [ ] An automation rule switches the FanControl profile and switches back after its condition ends.

## G. Network dashboard
- [ ] Phone/Steam Deck pairs with the PIN, shows dashboards live; profile switch works when allowed.
- [ ] Turning the feature off stops the page within a few seconds.

## H. Replay, backup, updates (Phase 18)
- [ ] Record a 1-minute replay; open it; loops; separate window/profile; closing leaves no extra SensorDeck.Collector process.
- [ ] `samples\amd-laptop-report.json` replay: AMD/NVIDIA/battery default dashboard, laptop alert limits.
- [x] Save backup → change something → restore → restarts with the change undone; phone stays paired.
- [ ] `sensordeck-cli diff` on two hardware reports.

## I. Without admin / without PawnIO (optional, informative)
- [ ] Run the collector from a normal (non-admin) terminal on another pipe:
      `src\SensorDeck.Collector\bin\Release\net10.0-windows\SensorDeck.Collector.exe --pipe SensorDeck-test` and
      `sensordeck-cli connect --pipe SensorDeck-test`: source status says "not elevated"; fewer sensors; nothing crashes.

## J. 24-hour soak
1. Normal use for a day with a dashboard window or sidebar open and the main window in the tray.
2. In PowerShell (elevated, to include the service):
   `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Soak-Monitor.ps1 -Hours 24`
- [ ] `Desktop\sensordeck-soak.csv`: memory and handles flat (no steady climb); collector CPU < 1 %, app < 2 % on average at a 1 s poll interval (column `cpu_percent`, share of the whole machine; `cpu_one_core_percent` is the same as a share of one core). GDI/USER objects of the app flat. The script warns when it isn't elevated or can't read the collector: no collector rows means the run doesn't count.
- [ ] Disk: `%ProgramData%\SensorDeck\history` stays under its cap; logs under 100 MB.

## Not testable here (noted for the release notes)
- Other hardware (AMD CPUs/GPUs, ITE/Fintek boards, laptops): covered by replay of hardware reports; real-world reports welcome.
