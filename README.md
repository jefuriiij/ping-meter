# PingMeter

A tiny Windows 11 utility that embeds a live ping readout **inside the taskbar** itself — not a tray icon: color-coded latency, a sparkline of recent pings, packet loss, and hover stats — a permanent replacement for keeping `ping google.com -t` open in a terminal.

It also carries a small set of network tools for when the connection actually breaks: a DNS-cache flush, the full internet-repair sequence, and IPv4 DNS switching.

## Install

```powershell
winget install jefuriiij.PingMeter
```

Or download `PingMeter.exe` from the [latest release](https://github.com/jefuriiij/ping-meter/releases/latest) and put it somewhere permanent (see the [SmartScreen note](#windows-smartscreen-warning-on-downloaded-releases) below).

**Requirements:** Windows 11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Run it, then tick *"Start automatically when I turn on my PC"* in Settings.

## Features

- **Taskbar-embedded widget** next to the tray clock — on the primary taskbar, secondary-monitor taskbar(s), or all of them. It moves out of the way of other taskbar widgets automatically.
- **Color-coded latency** (defaults: green < 50 ms, yellow < 120 ms, red above; timeouts show `T/O`).
- **Sparkline** of the recent pings; lost pings draw as full-height red bars.
- **Packet loss** — a red percentage appears on a second line under the number whenever recent pings go missing, and disappears when the connection is clean. The widget keeps the same width either way, so nothing else in the taskbar shifts.
- **Preset targets with quick switch** — right-click to jump between google.com, 1.1.1.1, facebook.com, or any host you add.
- **Hover tooltip** with current / min / avg / max, loss for the recent window *and* since reset, plus the DNS servers currently in use.
- **Network event log** — timeouts, latency spikes, recoveries and hourly summaries in daily log files, so you can check after the fact whether the connection was unstable. Optional raw per-ping CSV for graphing.
- **Network tools** (below) — internet repair and DNS switching.
- **Dark UI**, per-monitor DPI aware, survives Explorer crashes and restarts (re-embeds itself), and runs **without admin rights**.
- **Update checks** — manual, plus an optional quiet daily check that notifies you when a new release is out.

## Usage

- **Right-click** the widget (or the tray icon): switch target · Pause · Reset (clear stats and start fresh) · Fix internet… · View connection log · Open logs folder · Check for updates · Settings · Exit.
- **Double-click** either one: open Settings.
- Settings open in a Windows 11 Fluent window with three pages: **General** (targets, ping interval, colors, mini graph, autostart), **Advanced** (timeout, statistics period, which screens, see-through mode, logging, updates) and **Network tools**. Every option has a plain-English explanation under it and a longer tooltip on hover.
- Settings are stored at `%APPDATA%\PingMeter\settings.json`.

## Network tools

Everything here is optional, started by you, and prompts for Windows permission — PingMeter itself never runs elevated.

- **Quick fix — clear DNS cache**: `ipconfig /flushdns`. Instant, no admin prompt, fixes most "website not found" problems.
- **Full reset — rebuild the connection**: the classic sequence (flush DNS → release IP → renew IP → reset Winsock → reset TCP/IP), with a live progress bar, a per-step ✓/✗ activity log, and a restart countdown you can cancel. Steps that only take effect after a reboot say so explicitly.
- **DNS server**: shows the active adapter's current IPv4 DNS, and switches it between *Automatic (from your router)*, Cloudflare, Google, Quad9, or addresses you type. Switching back to Automatic undoes everything.
- **DNS over HTTPS**: per-server encryption, with the same three choices Windows 11 offers — *Off*, *On (automatic template)*, and *On (manual template)* for a URL you supply. Leave it alone and PingMeter won't touch your existing encryption settings.
- **Save your own combinations**: name a DNS pair (with its encryption choices) and it joins the dropdown for one-click reuse later.

## Logs

Daily files in `%APPDATA%\PingMeter\logs\` (auto-deleted after the retention window, default 30 days):

- `events-YYYY-MM-DD.log` — readable event log: ping timeouts, recovery (with outage duration), latency degraded/normal transitions, hourly min/avg/max/loss summaries, target switches, and network-tool actions.
- `samples-YYYY-MM-DD.csv` — optional (off by default): every ping as `timestamp,target,ms`, empty ms for timeouts. ~3.5 MB/day at a 1 s interval.

## Privacy

PingMeter collects nothing and sends nothing about you. It makes exactly two kinds of network requests: ICMP pings to the host *you* choose, and (if update checks are enabled) a daily read of this repository's latest-release info from the GitHub API. Logs and settings stay on your machine under `%APPDATA%\PingMeter`. Network changes only happen when you click the button and approve the Windows permission prompt.

## Windows SmartScreen warning on downloaded releases

Running a downloaded `PingMeter.exe` may show **"Windows protected your PC"**. This is normal for new, unsigned open-source executables — SmartScreen flags any exe without a code-signing certificate or established download reputation, regardless of whether it's safe. Click **More info → Run anyway** to proceed, or install via winget, which doesn't show it. Each release page lists the exe's SHA-256 so you can verify your download (`Get-FileHash PingMeter.exe`), and you can always build from source instead.

## Build from source

```powershell
cd app
dotnet run --project src/PingMeter          # run it

dotnet publish src/PingMeter -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
# -> src/PingMeter/bin/Release/net10.0-windows/win-x64/publish/PingMeter.exe
```

## Notes

- Stack: C# / .NET 10 — WinForms for the taskbar widget and tray, WPF ([WPF-UI](https://github.com/lepoco/wpfui)) for the Fluent settings window. The widget lives in the taskbar by reparenting its window (`SetParent`) into `Shell_TrayWnd` / `Shell_SecondaryTrayWnd`.
- Windows could break this in a future taskbar rewrite — if embedding ever fails, the widget falls back to floating above the taskbar and keeps retrying.
- Designed for the Windows 11 XAML taskbar. On a classic (Windows 10 / ExplorerPatcher) taskbar the widget still shows, but may overlap task buttons on a very full taskbar.
- In see-through mode, clicks land only on the visible pixels (the number and graph); the tray icon always works as a fallback.

## License

[MIT](LICENSE)
