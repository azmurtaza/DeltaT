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

- Windows 10/11 (64-bit)
- Administrator rights — CPU temperature registers need a kernel driver, same as HWiNFO/HWMonitor

## Install

Run **`DeltaT-Setup-<version>.exe`** and follow the wizard. It's a self-contained
build — no .NET install needed on the target machine. The setup:

- installs to `Program Files\DeltaT` and adds Start Menu + (optional) desktop shortcuts;
- optionally registers a **sign-in startup task** so DeltaT launches straight into the
  system tray every login. The task runs with highest privileges (elevated, so it gets
  CPU temps) *without* a UAC prompt each login, and is laptop-safe (keeps running on
  battery). Uncheck that box during setup if you'd rather start it manually;
- leaves your learned thermal history and settings in `%LOCALAPPDATA%\DeltaT` on
  uninstall, so a reinstall keeps the machine's baseline.

Launching the app while it's already running just surfaces the existing window
(single-instance). Closing the window minimizes to the tray; quit from the tray menu.

## Build

```
dotnet build DeltaT.sln
dotnet run --project src/DeltaT.App     # the app (run elevated)
dotnet run --project src/DeltaT.Spike   # raw sensor dump for diagnostics
```

Requires the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

## Package the installer

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Publishes a self-contained `win-x64` build and compiles `installer\DeltaT.iss` into
`installer\out\DeltaT-Setup-<version>.exe`. Needs Inno Setup 6
(`winget install --id JRSoftware.InnoSetup`). The version comes from `<Version>` in
`src/DeltaT.App/DeltaT.App.csproj` — bump it there to cut a new release.
