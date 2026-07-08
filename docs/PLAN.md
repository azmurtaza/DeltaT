# DeltaT — your machine's thermal conscience

A lightweight Windows background app that learns your machine's thermal fingerprint, corrects for the weather outside, and tells you — with evidence — when it's time to repaste your CPU/GPU.

## Context

Thermal paste degrades silently over 1–3 years. Absolute temperatures can't tell you paste health because ambient temperature dominates (a 45°C summer day makes healthy paste look dead; an air-conditioned room hides dying paste). The correct signal is **temperature rise over ambient, at a given load, compared against this specific machine's own healthy baseline**. No mainstream tool does this. DeltaT will.

Verified environment: **Acer Nitro V 15 (ANV15-51)** — i5-13420H, RTX 3050 6GB Laptop + Intel UHD iGPU, battery present (laptop auto-detected via WMI). Empty project folder, `git` + `winget` available, .NET runtime present but **no SDK** (will install). Windows 11 Home.

## Platform & stack (chosen for "lightweight + optimized")

- **.NET 8 (LTS) + WPF**, single process, tray-resident. Windows-only by nature (the sensor layer is Windows-specific).
- **Why not Electron/Tauri**: Electron idles at 150–300MB RAM; Tauri has no credible Windows sensor story. The only battle-tested sensor library (**LibreHardwareMonitorLib**) is .NET — same-process native is the lightest possible architecture.
- **Sensors**: LibreHardwareMonitorLib — CPU (per-core + package), GPU (dGPU + iGPU), NVMe/SSD, motherboard, fans, battery — whatever the hardware exposes. Requires **admin** (kernel driver for CPU registers — same as HWiNFO/HWMonitor).
- **Charts**: ScottPlot 5 (fast real-time WPF plots) for big time-series; **custom-drawn WPF gauges, dials, and sparklines** for the dashboard — full visual control, no stock-library look.
- **Storage**: SQLite (`Microsoft.Data.Sqlite`), single file, batched writes.
- **Weather**: Open-Meteo (free, no API key) for current outside temp; Open-Meteo geocoding for manual city entry; ip-api.com for one-shot auto-location.
- **Tray**: Hardcodet.NotifyIcon.Wpf. **Toasts**: Windows notifications for important events only.

**Performance budget (hard targets)**: < 0.5% average CPU while minimized, < 150MB RAM, DB < 50MB/year. Charts and UI rendering fully pause while hidden; sensor loop (2s interval, configurable) is the only background work; DB writes batched every 30s.

## Architecture

```
DeltaT.sln
├─ src/DeltaT.Core/          # no UI deps — fully unit-testable
│   Monitoring/              # LHM wrapper, snapshot loop, load buckets, throttle detection
│   Storage/                 # SQLite repo, downsampling, retention
│   Weather/                 # ambient service: locate once, refresh temp every 3h, cache
│   Machine/                 # WMI identity (make/model/laptop-vs-desktop), silicon limits
│   Knowledge/               # embedded thermal profile DB (JSON)
│   Scoring/                 # baseline learning, paste health score, trends
│   Remarks/                 # rule engine with personality + cooldowns
├─ src/DeltaT.App/           # WPF: Views, ViewModels, custom Controls, dark theme, tray
└─ tests/DeltaT.Core.Tests/  # scoring + aggregation unit tests
```

**Data flow**: sensor snapshot every 2s → ring buffer (last hour, in memory) → batched to SQLite → aggregator downsamples (raw kept 48h → per-minute kept 90 days → per-hour kept forever) → scoring engine → dashboard/remarks via events.

**Load-bucketed stats** (your "CPU temp under 100% usage" idea, generalized): every sample is tagged idle (<10%), light (10–40%), medium (40–70%), heavy (70–100%) per component. All stats (avg/min/max/delta) are tracked per bucket.

## The brain — how DeltaT decides

1. **Baseline learning (confidence-gated, not a fixed timer)**: records delta-over-ambient per component per load bucket, heat-soak rate (how fast temp spikes when load hits), fan speeds (if exposed), throttle events. The score stays "calibrating" until the baseline is *statistically* confident — the standard error of independent session means falls below target for the loaded buckets — gated by a paste break-in ramp (~3.5+ days minimum). Feeding it varied load locks it sooner; leaving it idle keeps it learning. Absolute-limit warnings work from day 1.
2. **Ambient correction**: location resolved once (auto by IP, or typed manually) and cached — exactly as you specified, no continuous polling. The outside *temperature* refreshes every 3h (cheap single GET; weather changes even when location doesn't). Offline → last cached value with staleness note. Optional "indoor offset" setting for heavily air-conditioned rooms. Baselines are bucketed by ambient band (<15 / 15–25 / 25–35 / >35°C) so summer-vs-winter comparisons stay fair.
3. **Safe limits — three layers (your fallback chain, preserved)**:
   - **The silicon itself** (primary): CPU TjMax read from the chip (100°C on your i5-13420H), GPU throttle point where exposed. More authoritative than any benchmark site.
   - **Curated model/brand profiles** (embedded JSON): exact model → brand + series (e.g. "Acer Nitro gaming laptop") → category (generic gaming laptop / thin-and-light / desktop). Gaming laptops sustaining 90–95°C under load get flagged as *by design*, not broken paste.
   - **One honest change from your brief**: no *runtime* web-scraping of per-model benchmarks in v1 — there is no reliable free API for it, and scrapers break constantly. Instead **I do the benchmark research at build time** (published Nitro V 15 review thermals etc.) and bake it into the profile DB. Your fallback chain still works exactly as described; it just runs offline and never breaks. A hook stays in place for a future online profile DB.
4. **Paste Health Score, 0–100, per component** — CPU and GPU get paste scores; SSD/battery/board get thermal-health readouts only (no paste in them). Score inputs: current vs baseline delta (same ambient band + load bucket), throttle-event frequency, heat-soak rate change, fan-speed creep, distance to TjMax, multi-week trend slope. Every score **explains itself**: "+6°C over baseline at heavy load in similar weather, 3 throttle events this week."
   - **85–100 Fresh** · **70–84 Good** · **50–69 Aging — watch it** · **30–49 Degraded — plan a repaste** · **<30 Repaste now**
5. **Dust-vs-paste insight**: degraded paste shows fast heat-soak + throttle spikes; dust/blocked airflow shows high steady-state + elevated fans at normal soak rate. DeltaT says which pattern it sees, so you don't repaste when you just need compressed air.
6. **Repaste log**: tell DeltaT you repasted → baseline resets → after a few days it shows your before/after gains ("−8°C at heavy load. Money well spent.").
7. **Thermal fingerprint test (on-demand)**: guided ~5-minute check — built-in CPU load generator (with big stop button + duration cap), guided GPU load (run any game/benchmark; DeltaT detects it) — producing a repeatable score you can rerun monthly instead of waiting on passive data.

## UI/UX

**Design language**: dark engineering-console aesthetic — near-black blue-grey, one thermal gradient used semantically everywhere (cyan = cool → amber → red = hot), Cascadia Code for numerals (ships with Win11), custom ring gauges and score dials with subtle glow, smooth 60fps micro-animations, zero stock-framework styling. Remarks written in DeltaT's voice — dry, precise, occasionally funny — every temp range has bespoke copy. The details that make it feel hand-built: per-component iconography, °C/°F toggle, weather chip showing your city + outside temp, "calibrating" progress ring during learning week.

**Screens**:
1. **Dashboard** — hero paste-health verdict, live per-component cards (current temp + load + 10-min sparkline + mini score), ambient/weather chip, live remarks ticker.
2. **Component detail** — real-time dual-axis chart (temp + load), avg/min/max by timeframe, delta-over-ambient, score breakdown ("why this number").
3. **Trends** — 24h / 7d / 30d / all, ambient overlay, month-vs-month comparison, throttle-event markers, repaste markers.
4. **Remarks feed** — timeline of everything DeltaT noticed.
5. **Settings** — location, units, sampling interval, autostart, notifications, indoor offset, data retention, machine info panel, CSV export.
6. **First-run onboarding** — detects your machine, asks location once, explains the learning week.

**Background behavior**: close button minimizes to tray (setting), tray icon shows hottest component temp live, tray menu (open / pause monitoring / snooze notifications / quit). Autostart via **Task Scheduler with highest privileges** — this is how the admin requirement starts silently at logon with no UAC prompt.

## Build phases (each ends with something you can run/see; git commit per phase)

- **P0 — Toolchain + sensor spike**: install .NET 8 SDK (winget), `git init` + .gitignore (bin/obj excluded — also calms OneDrive sync churn), scaffold solution, console spike that dumps **every sensor LHM finds on your Nitro**. *Checkpoint: your real temps in a console.*
- **P1 — Monitoring core**: snapshot loop, load buckets, throttle detection, SQLite storage + downsampling, WMI machine identity.
- **P2 — Ambient**: location + weather service with cache/refresh/offline fallback.
- **P3 — The brain**: baseline learning, scoring engine, remarks engine — pure logic, unit-tested against simulated degraded-paste scenarios.
- **P4 — UI shell**: dark theme, dashboard with live cards/gauges/sparklines, tray integration, close-to-tray.
- **P5 — Full app**: component detail + trends + remarks feed + settings + onboarding + repaste log + fingerprint test + toasts + autostart.
- **P6 — Polish & verify**: perf pass against budget, simulated-sensor demo mode, publish single-file exe (portable to your desktop PC later), verification checklist.

## Risks & honest limitations

- **Admin required** for CPU temps (kernel driver) — unavoidable, same as every serious monitor. Autostart handles it gracefully.
- **Battery temp**: Windows rarely exposes it; shown only if your ACPI does (on the Nitro, likely not). Same caveat for laptop fan RPM and "system/board" temp — laptops hide these behind the EC. CPU/GPU/SSD — the ones that matter for paste — are reliably available.
- **Dual GPU**: RTX 3050 is the paste-relevant GPU; iGPU shown collapsed.
- **Confidence-gated calibration** (standard error of session means per bucket + paste break-in ramp, ~3.5+ days for a well-used machine) before scores are confident; absolute warnings active immediately.
- **OneDrive folder**: builds inside a synced folder can get flaky with file locks; .gitignore mitigations first, and if it fights us I'll suggest relocating to `C:\dev`.

## Verification

- **Unit tests**: scoring engine against synthetic scenarios (e.g. +8°C delta at heavy load in same ambient band must drop score below 50; hot-summer-day scenario must *not*).
- **Sensor truth**: cross-check DeltaT's GPU temp vs `nvidia-smi`, CPU behavior under a real load spike.
- **Perf budget**: measure working set + CPU% over 10 minutes minimized.
- **Ambient**: fetched temp vs actual weather for your city; offline mode by disabling Wi-Fi.
- **Demo mode**: simulated sensor source flag to preview degraded/healthy states in the UI without waiting a week.

## Post-v1 parking lot

Online community profile DB, multi-machine sync/export-import (laptop + desktop history in one view), undervolting suggestions, fan-curve awareness, localization.
