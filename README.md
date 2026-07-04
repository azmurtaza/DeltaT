# DeltaT

**Your machine's thermal conscience.**

DeltaT runs quietly in the system tray, watches your CPU / GPU / SSD temperatures against the weather outside, learns what *healthy* looks like for your specific machine, and tells you — with evidence — when it's time to change your thermal paste.

## Why it's different

Raw temperatures lie. A 45 °C summer afternoon makes healthy paste look dead; an air-conditioned room hides paste that's drying out. DeltaT scores paste health on **temperature rise over outside ambient, at a given load, compared to your machine's own learned baseline** — the signal that actually tracks paste degradation.

- **Load-bucketed stats** — idle / light / medium / heavy tracked separately per component
- **Paste Health Score 0–100** for CPU and GPU, with plain-language reasons ("+6 °C over baseline at heavy load in similar weather")
- **Dust-vs-paste insight** — tells you when you just need compressed air, not a repaste
- **Repaste log** — mark a repaste, see your before/after gains a few days later
- **Thermal fingerprint test** — guided 5-minute check you can rerun monthly
- **Weather-aware** — location resolved once, outside temp refreshed every 3 h (Open-Meteo, no account needed)

## Requirements

- Windows 10/11
- .NET 8 (`winget install Microsoft.DotNet.SDK.8` to build; runtime enough to run)
- Administrator rights — CPU temperature registers need a kernel driver, same as HWiNFO/HWMonitor

## Build

```
dotnet build DeltaT.sln
dotnet run --project src/DeltaT.App     # the app (run elevated)
dotnet run --project src/DeltaT.Spike   # raw sensor dump for diagnostics
```
