# SpaceDeck — Phase 12 test plan

Work through the sections in order; tick each box or note what happened. Use the latest Release build
(`dotnet build SpaceDeck.slnx -c Release`) and the installed collector service (`scripts\Install-Collector.ps1`).
"Expected" is what should happen; anything else is a bug to report (a screenshot helps).

## A. Automated tests
- [x] `dotnet test` — all pass (202/202, 2026-10-23).

## B. Fresh profile (first run)
1. Exit SpaceDeck. Rename `%AppData%\SpaceDeck` to `SpaceDeck.bak`.
2. Start SpaceDeck.
- [x] Opens on Diagnostics with the welcome box; checks are green/notes except things genuinely missing.
- [x] "Looks good" → a default dashboard built from your hardware; default alert rules on the Alerts page.
3. Exit, delete the new `%AppData%\SpaceDeck`, rename `SpaceDeck.bak` back. Start again: everything as before.

## C. Collector robustness (app keeps running throughout)
- [x] Restart the service (elevated: `Restart-Service SpaceDeckCollector`): banner "Connecting…", then live again within ~5 s; history charts continue.
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
- [ ] Record a 1-minute replay; open it; loops; separate window/profile; closing leaves no extra SpaceDeck.Collector process.
- [ ] `samples\amd-laptop-report.json` replay: AMD/NVIDIA/battery default dashboard, laptop alert limits.
- [x] Save backup → change something → restore → restarts with the change undone; phone stays paired.
- [ ] `spacedeck-cli diff` on two hardware reports.

## H2. Cooler screen (Lian Li GA II LCD, FanControl with SpaceDeck's Lian Li plugin with screen support)
- [ ] `spacedeck-cli screen-test --hardware ?` lists `lianli:/LianLi/0  … 480 × 480`; `--hardware lianli:/LianLi/0`
      shows the colour bars and the time on the cooler, the frame number counting up; ~20 s after it ends the cooler
      shows its own screen again. Diagnostics → "Cooler screen pipe" counts the frames and shows the frames a second.
- [ ] `screen-test --hardware lianli:/LianLi/0 --fps 30 --seconds 60`: the orange bar sweeps across the colour bars,
      the line printed each second shows the achieved fps and the collector's drops, and `plugin.log` gets one
      "screen frames in the last minute" line (sent, dropped, write ms, size). Fan and pump control stay on their
      curves throughout. At `--fps 24` and `--fps 30` the collector sends on 24 / 30 a second (not ~21) and drops
      next to nothing. `--quality 50` shows in the start line and makes smaller frames; `--fps 0`, `--fps 31`,
      `--quality 29` and `--quality 101` are refused.
- [ ] With the app's cooler screen on, `screen-test` warns once that another sender (SpaceDeck) holds the screen, and
      the cooler shows only the app's frames (no flicker between the two); Diagnostics shows "refused (another
      sender) · from SpaceDeck". With the app's screen off, the test takes the screen 3 s after the app's last frame.
- [ ] Settings → Cooler screen: the cooler is listed, Off. Turning it on makes the "Cooler screen" dashboard and the
      preview and the cooler show it within the interval; rotation, interval and quality apply to the next frame.
- [ ] Turning it off, closing SpaceDeck and stopping the collector service each bring the cooler's own screen back
      within ~25 s; fan and pump control in FanControl are unaffected throughout.
- [ ] Switch skins while it runs: the next frame uses the new skin. The main window stays responsive.

## H3. Cooler screen pictures, GIFs, videos, picture tiles, square editor
Rotation "Upside down" for the owner's cooler; every check also on "Upright" once. The status line under the screen
reads "On · N fps · X KB" while frames go out at a frame rate, else "On · … · X KB, sent hh:mm:ss".
- [ ] **Picture:** Show → "A picture", Choose… a JPEG photo taken on a phone (portrait): it shows upright on the cooler
      (EXIF orientation applied), Fill crops top and bottom, Fit shows it whole with bars in the chosen background
      colour (try White). The preview in Settings shows it upright. The status says a still picture is sent again every
      5 s; Diagnostics' frames-a-second stays near 0.2. Rename or delete the original file: nothing changes (SpaceDeck
      keeps its copy in `%AppData%\SpaceDeck\screens`).
- [ ] **Colour-bar edge line** (the one-pixel row of the neighbouring bar's colour seen with `screen-test`): show
      `samples/screen-test-colour-bars.png` as a Picture (Fill, Upright, so it matches the unrotated CLI frames). The
      app's JPEGs are WPF's (4:2:0, 16 × 16 blocks), the CLI's are 4:4:4 (8 × 8). If the line is gone here, the cooler's
      decoder mishandles 4:4:4 at the picture's first row; if it is there too, it is the cooler's display. Either way
      the CLI's encoder decodes cleanly elsewhere (row 0 equals row 5 in every bar).
- [ ] **GIF:** "An animated GIF or a video" → an optimized GIF (one from a site like Giphy, with small changing
      rectangles and transparency): no garbage, trails or flicker on the cooler, it loops, and runs at its own speed
      (a 10 fps GIF shows ~10 fps in the status line; one at 30+ fps shows the max frames a second). Max frames a
      second 5: visibly choppier; 24 and 30: smooth, Diagnostics shows next to no drops.
- [ ] **Video:** an MP4 (H.264, 1080p), a MOV from a phone, a WMV and an AVI: each plays smoothly at 24 fps, loops
      without a long pause, no sound. A file Windows can't play (e.g. a .mkv with a codec not installed) gives "Couldn't
      play …" in the status line and nothing on the cooler (its own screen comes back after ~20 s). The main window
      stays responsive while a video plays (drag the window, switch pages); Task Manager: CPU use is moderate.
- [ ] **Slideshow:** a folder with a few pictures and a GIF, 5 seconds each: they follow in name order, the GIF
      plays during its turn, and it loops. A broken file in the folder shows "Skipped …" and the background colour for
      its turn.
- [ ] **Stopping:** with a video playing, untick "Use this screen", close SpaceDeck, and stop the collector service, one
      at a time: the frames stop at once (Diagnostics' count stops rising) and the cooler shows its own screen again
      after ~20 s. `screen-test` while a video plays is refused (another sender), as with a dashboard.
- [ ] **Picture tile:** Add tile → "Picture or GIF" → Choose… a GIF: it animates on the dashboard; Fit inside / 40 %
      opacity apply; it animates on the cooler too when that dashboard is shown there (status at ~the max fps).
- [ ] **Background:** dashboard … menu → "Background picture or GIF…": it fills the page behind all tiles; "Fit the
      background inside" and "Dim the background" apply; on a cooler screen layout it fills the whole cooler screen,
      edges included. "Remove the background" brings the skin back.
- [ ] **Square editor:** Settings → Cooler screen → "Edit layout": the dashboard page opens in edit mode with an
      orange frame of the screen's shape (6 × 11 cells), the caption under it, and beside it the live preview of what the
      cooler shows, upright, updating as tiles move. Tiles inside the frame can't be dragged past its right edge; one
      dragged below it disappears from the cooler. The dashboard … menu's "Square layout for a cooler screen" marks any
      dashboard so it is edited this way.
- [ ] **Default layout:** Settings → Cooler screen → "Reset to default layout…" on the existing "Cooler screen"
      dashboard: after the confirmation, CPU temperature large across the top, GPU and coolant side by side, pump across
      the bottom, filling the screen, readable at arm's length. Another dashboard, a skin, the background and the name
      stay as they were. A cooler turned on for the first time on a fresh profile gets this layout.

## I. Without admin / without PawnIO (optional, informative)
- [ ] Run the collector from a normal (non-admin) terminal on another pipe:
      `src\SpaceDeck.Collector\bin\Release\net10.0-windows\SpaceDeck.Collector.exe --pipe SpaceDeck-test` and
      `spacedeck-cli connect --pipe SpaceDeck-test`: source status says "not elevated"; fewer sensors; nothing crashes.

## J. 24-hour soak
1. Normal use for a day with a dashboard window or sidebar open and the main window in the tray.
2. In PowerShell (elevated, to include the service):
   `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Soak-Monitor.ps1 -Hours 24`
- [ ] `Desktop\spacedeck-soak.csv`: memory and handles flat (no steady climb); collector CPU < 1 %, app < 2 % on average at a 1 s poll interval (column `cpu_percent`, share of the whole machine; `cpu_one_core_percent` is the same as a share of one core). GDI/USER objects of the app flat. The script warns when it isn't elevated or can't read the collector: no collector rows means the run doesn't count.
- [ ] Disk: `%ProgramData%\SpaceDeck\history` stays under its cap; logs under 100 MB.

## Not testable here (noted for the release notes)
- Other hardware (AMD CPUs/GPUs, ITE/Fintek boards, laptops): covered by replay of hardware reports; real-world reports welcome.
