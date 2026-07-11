# DeltaT

**Your machine's thermal conscience.**

DeltaT sits in your system tray, watches your CPU, GPU, and SSD temperatures relative to the weather outside, and learns what normal looks like for your specific machine. When something starts drifting, it tells you why and whether it is actually time to repaste.

## Why it is different

Raw temperatures are misleading. A 45 C summer afternoon can make healthy paste look dead, while an air-conditioned room can hide paste that is quietly drying out. DeltaT scores paste health based on temperature rise over outside ambient at a given load, compared to your machine's own learned baseline. That is the number that actually tracks paste degradation over time.

Fan speed is also factored in, so switching between silent and performance fan modes does not get misread as paste health changing. On laptops the fans usually hide behind the embedded controller where standard sensor tools cannot see them; on supported gaming machines (Acer Nitro and Predator, Lenovo Legion and LOQ) DeltaT reads CPU and GPU fan RPM straight from the vendor firmware, read-only, so it never touches your fan curves.

## What it does

- **Paste Health Score 0-100** for CPU and GPU - compares current temp rise over ambient, per load bucket, against your machine's own baseline, not a spec sheet or benchmark database
- **Load-bucketed stats** - idle, light, medium, and heavy load tracked separately so a brief gaming session does not skew your baseline
- **SSD, battery, and board** - thermal health and wear readouts (SMART wear level, battery wear) alongside the paste scores
- **Dust vs. paste insight** - fast heat spikes read as paste degradation; high steady temps with elevated fans at normal soak rate read as dust or airflow. DeltaT tells you which pattern it sees, so you know whether to grab compressed air or open the machine
- **Repaste verdict** - after you log a repaste and the new baseline settles, DeltaT compares before and after like-for-like (same load bucket, same ambient band, fan-normalized) and calls it Improved, Unchanged, Worse, or Inconclusive. A worse result (air bubble, bad mount, pump-out) raises a visible warning, not just a quiet note
- **History and trends** - full local history with 24h, 7d, 30d, and all-time graphs (scroll to zoom, drag to pan), plus a monthly score readout so you can see how this month compares to 30 days ago
- **Staleness detection** - if DeltaT has not run for 45 days or more with a locked baseline, it flags the score as unverified and offers a one-click Recalibrate in Settings. Your old baseline is never auto-deleted
- **Provisional score while it learns** - instead of a blank dial for the first week, DeltaT shows an estimated score with a confidence readout the moment there is enough load to compare, and it locks the real score by statistical confidence rather than a fixed countdown
- **Automatic updates** - DeltaT checks its own GitHub releases on startup and installs new versions quietly, so you are never stuck on an old build. Turn it off in Settings, or check on demand with a button
- **Weather-aware** - outside temp refreshes every 3 hours via Open-Meteo. No account needed

## How the score works

DeltaT does not lock a final score on a fixed timer. It calibrates by confidence: it watches how precisely it knows your machine's normal temperature rise at each load level, session by session, and only locks the baseline once that reading is statistically solid and the fresh paste has had a few days to settle. Stress the machine with games or heavy work and it locks sooner; leave it idle and it keeps learning rather than guessing on data it has never seen.

While it calibrates it still shows a provisional estimate the moment there is real load to compare, marked with how confident it is so far, so you are never staring at a blank dial. Absolute temperature limit warnings (proximity to TjMax) are active from the start regardless. Once the baseline locks, every new reading is compared against your machine's own history, bucketed by load and ambient temperature, so seasonal changes and airflow differences do not create false alarms.

## Requirements

- Windows 10 or 11 (64-bit)
- Intel or AMD (Ryzen) CPU, including recent parts like Intel Core Ultra and AMD Ryzen 7040/8040 series - DeltaT reads the hottest core temperature either vendor exposes
- Administrator rights - CPU temperature registers and storage SMART data require a kernel driver, the same as HWiNFO or HWMonitor. Non-elevated runs will miss CPU package temps and drive health

## Install

Run **`DeltaT-Setup-<version>.exe`** and follow the wizard. It is a self-contained build, so no separate .NET install is needed on the target machine. The setup:

- installs to `Program Files\DeltaT` and adds Start Menu and optional desktop shortcuts
- optionally registers a sign-in startup task so DeltaT launches straight into the system tray on every login. The task runs with elevated privileges so it can read CPU temps without a UAC prompt each time, and it is laptop-safe so it keeps running on battery. Uncheck that box during setup if you prefer to start it manually
- leaves your learned thermal history and settings in `%LOCALAPPDATA%\DeltaT` on uninstall, so reinstalling keeps the machine's baseline intact

Once installed, DeltaT keeps itself up to date. On startup it checks its GitHub releases and quietly installs any newer version, then restarts into the tray. You can turn that off in Settings and check on demand instead.

Launching the app while it is already running just brings up the existing window. Closing the window minimizes to tray. Quit from the tray menu.

## Build

```
dotnet build DeltaT.sln
dotnet test
dotnet run --project src/DeltaT.App     # run elevated for full sensor access
dotnet run --project src/DeltaT.Spike   # dumps every sensor to console for diagnostics
```

Requires the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

## Package the installer

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Publishes a self-contained `win-x64` build and compiles `installer\DeltaT.iss` into `installer\out\DeltaT-Setup-<version>.exe`. Needs Inno Setup 6 (`winget install --id JRSoftware.InnoSetup`). The version is pulled from `<Version>` in `src/DeltaT.App/DeltaT.App.csproj`, so bump it there when cutting a new release.
