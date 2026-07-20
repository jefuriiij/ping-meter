# PingMeter

A tiny Windows 11 utility that embeds a live ping readout **inside the taskbar** itself — not a tray icon: color-coded latency, a sparkline of recent pings, and hover stats — a permanent replacement for keeping `ping google.com -t` open in cmd.

## Features

- **Taskbar-embedded widget** next to the tray clock, on the primary taskbar, secondary-monitor taskbar(s), or all of them.
- **Preset targets with quick switch** — right-click the widget or tray icon to jump between google.com, 1.1.1.1, facebook.com, or any host you add.
- **Color-coded latency** (defaults: green < 50 ms, yellow < 120 ms, red above; timeouts show `T/O`).
- **Sparkline** of the last ~24 pings; lost pings draw as full-height red bars.
- **Hover tooltip** with min / avg / max / packet-loss over the stats window.
- **Network event log** — timeouts, latency spikes, recoveries, and hourly summaries written to daily log files, so you can check after the fact whether your connection was unstable. Optional raw per-ping CSV for graphing.
- **Check for updates** — manually from the menu, plus an automatic daily check (toggleable) that notifies you when a new release is out.
- Survives Explorer crashes/restarts (re-embeds automatically), tracks DPI per monitor, needs **no admin rights**.

## Run

```powershell
cd app
dotnet run --project src/PingMeter
```

## Build a single exe

```powershell
cd app
dotnet publish src/PingMeter -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
# -> src/PingMeter/bin/Release/net8.0-windows/win-x64/publish/PingMeter.exe
```

## Usage

- **Right-click** the widget or tray icon: switch target, pause, view log, open logs folder, check for updates, settings, exit.
- **Double-click** either: open settings (targets, interval, timeout, thresholds, monitor placement, logging, autostart).
- Settings are stored at `%APPDATA%\PingMeter\settings.json`.

## Logs

Daily files in `%APPDATA%\PingMeter\logs\` (auto-deleted after the retention window, default 30 days):

- `events-YYYY-MM-DD.log` — readable event log: ping timeouts, recovery (with outage duration), latency degraded/normal transitions, hourly min/avg/max/loss summaries, target switches.
- `samples-YYYY-MM-DD.csv` — optional (off by default): every ping as `timestamp,target,ms`, with an empty ms for timeouts. ~3.5 MB/day at a 1 s interval.

## Notes

- Stack: C# / .NET 8 WinForms. The embedding technique (SetParent into `Shell_TrayWnd` / `Shell_SecondaryTrayWnd`) keeps the widget next to the tray clock.
- Windows can theoretically break this in a future taskbar rewrite — if embedding ever fails, the widget falls back to floating on top of the taskbar and keeps retrying.
- Designed for the Windows 11 XAML taskbar. On a classic (Win10/ExplorerPatcher) taskbar the widget still shows but may overlap task buttons on a very full taskbar.
