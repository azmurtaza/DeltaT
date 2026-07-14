# DeltaT

**Your PC's thermal diagnostician.**

DeltaT sits in your system tray, watches your CPU, GPU, and SSD temperatures against the weather outside, and learns what normal looks like for your specific machine. When something starts drifting, it does not just tell you the number moved. It tells you **what is actually driving it**: dried out paste, dust and airflow, a fan that is failing, a cooler that has come loose, an overclock you asked for, or simply a hot room.

## Why it is different

Most tools stop at "your temps are high". That is the easy half. The hard half is why, because a hot machine has many possible causes and only one of them is fixed by taking the cooler off.

DeltaT separates them by physics. Degraded paste raises the temperature only under load, makes the heat soak in faster, and makes it leave slower. Dust lifts every reading including idle, keeps the soak rate normal, and makes the fans work harder for the same result. A failing fan runs below the speed your own machine has always used at that load. A bad mount pulls the hottest point on the die away from the edge. An overclock moves the temperature because the wattage moved, not because the cooling changed. DeltaT watches all of those signals, weighs the evidence, and gives you a ranked diagnosis in plain English with the reasoning shown.

Raw temperatures are also misleading on their own. A 45 C summer afternoon can make healthy paste look dead, while an air conditioned room can hide paste that is quietly drying out. Every reading DeltaT judges is corrected for the outside temperature, for fan speed, and for the wattage your chip was actually pulling, so the seasons, your fan profile, and your undervolt cannot masquerade as paste degradation.

## What it does

- **Cause diagnosis, the headline feature**: a ranked verdict with confidence and evidence, not a single number. Paste, airflow and dust, fan fault, cooler mount, power configuration, hot room, no cooling headroom left, or simply healthy. The dashboard shows the likely cause next to the score, and the remarks feed names it as it happens
- **Per aspect health matrix**: every subsystem scored 0 to 100 against your own machine's baseline. PASTE, AIRFLOW, FANS, MOUNT, HEADROOM, plus a POWER column that reads stock, overclocked, or undervolted from the wattage your chip is actually drawing. A healthy paste reads 100 while a choked airflow reads 0, instead of one paste flavoured verdict hiding everything else. Anything your hardware cannot measure reads as blank, never as a fake number
- **A measured accuracy claim, not a marketing one**: DeltaT ships a benchmark that injects known faults (degraded paste at several severities, dust, a failing fan, pump out, an overclock, an undervolt, a cold season) through the same physics a real machine obeys, then runs the real scoring and diagnosis engine against them. Current results: it catches essentially every injected fault, clears roughly 99 percent of the confounders without a false alarm, and attributes the cause correctly about 99.7 percent of the time. The numbers are reproducible and guarded by tests, so a regression fails the build
- **Power normalized scoring**: heat rise scales with watts, so DeltaT compares thermal resistance (degrees per watt), which is the quantity paste actually owns. An overclock that draws more power is not flagged as ageing paste, and an undervolt that lowers your temperatures cannot hide real degradation underneath
- **Cooldown tracking**: the same bad contact that slows heat into the die slows heat out of it, so DeltaT watches how fast your machine sheds heat when a game or a render finishes. It is the falling edge twin of the heat soak signal and it corroborates the paste verdict independently
- **Long term drift and step changes**: the score answers "hotter than normal right now". The trend answers "getting hotter week over week, and since when". DeltaT measures the slope in degrees per month and projects roughly how long until a repaste is worth doing, and separately detects a sudden step, the fingerprint of a discrete event like a knocked cooler or a fan that began to fail, and tells you which week to look at. Everything is season matched, so a warm July never reads as drift
- **Configurable warning limits**: overclockers can set their own concern temperature per component and switch off the near silicon limit warning for a rig that lives near its limit by design. Real throttle events are always counted and drift against your own baseline is always tracked, so turning down the noise never blinds the tool
- **Overall health score 0 to 100** for CPU and GPU: current temperature rise over ambient, per load bucket, against your machine's own learned baseline, never a spec sheet or a benchmark database
- **A fingerprint test for CPU and GPU that runs until the heat actually settles**: a short calm period, then full load held until the component stops climbing, while DeltaT watches how fast the heat soaks in and where it levels off. It is not a stopwatch, because the honest number is the steady state and every cooler reaches it at its own pace: a thin laptop is done in about a minute and a half, a desktop under a big air cooler is given the time it needs. The CPU test spins every core; the GPU test saturates the graphics card's compute engine directly, with no extra software to install, and it picks the real gaming GPU on hybrid laptops. Run it monthly and the weather corrected drift between runs is your cooling ageing. DeltaT nudges you when one is due
- **Hotspot gap tracking**: on GPUs that report a hotspot sensor, DeltaT learns your card's own normal hotspot to edge gap and treats a widening gap as the early warning it is, since pump out and dry out show up there long before the core reading moves
- **Fan speed awareness**: fan RPM is factored into every comparison, so switching between silent and performance modes does not get misread as paste health changing, and a fan running below the speed your machine has always used at that load is called out. On laptops the fans usually hide behind the embedded controller where standard tools cannot see them; on supported machines (Acer Nitro and Predator, Lenovo Legion and LOQ, ASUS ROG, TUF and Zenbook, and HP's business line of EliteBook, ProBook and Z) DeltaT reads CPU and GPU fan RPM straight from the vendor firmware, strictly read only, so it never touches your fan curves. If your chassis is not supported, DeltaT loses fan normalization and nothing else
- **Light enough to leave running while you game**: reading a sensor is not free, and the two readings DeltaT needs most often, CPU and GPU, happen to be the two that the standard sensor library is slowest at. So DeltaT reads those itself, straight from the CPU's own thermal registers and from the NVIDIA driver's own library, which is roughly a hundred times faster, and leaves the slow library only the handful of readings nothing else can provide, on a much slower clock. The whole monitoring loop now costs about 1 percent of a single core, down from 7 to 10 percent, which is what removes the frame hitches people saw when a sensor read landed in the middle of a frame. Every fast path falls back to the old one automatically if your hardware does not support it, so it is faster where it can be and never wrong
- **Background capture control**: if sensor polling bothers you mid game, you can turn it off entirely in Settings, or slow the sampling interval down
- **SSD, battery, and board**: thermal health and wear readouts (SMART wear level, battery wear) alongside the CPU and GPU scores
- **Repaste verdict**: after you log a repaste and the new baseline settles, DeltaT compares before and after like for like (same load bucket, same ambient band, fan and power normalized) and calls it Improved, Unchanged, Worse, or Inconclusive. A worse result, meaning an air bubble, a bad mount, or pump out, raises a visible warning rather than a quiet note
- **History and trends**: full local history with 24h, 7d, 30d, and all time graphs (scroll to zoom, drag to pan), plus a monthly readout comparing this month with 30 days ago
- **Staleness detection**: if DeltaT has not run for 45 days or more with a locked baseline, it flags the score as unverified and offers a one click Recalibrate. Recalibration is verification, not amnesia: your history is never touched, and if the machine turns out to behave exactly as it used to, the old reference is adopted straight back
- **Provisional score while it learns**: instead of a blank dial for the first week, DeltaT shows an estimated score with a confidence readout the moment there is enough load to compare, and locks the real score by statistical confidence rather than a countdown
- **Automatic updates**: DeltaT checks its own GitHub releases on startup and installs new versions quietly. Turn it off in Settings, or check on demand
- **Weather aware**: outside temperature refreshes every 3 hours via Open-Meteo. Location resolves from Windows positioning first (IP lookup only as a fallback) and names the nearest recognizable city rather than a small nearby village. No account needed
- **A remarks feed that actually watches**: short observations as things happen, including temperatures climbing or falling at similar load, a silent fan profile trading degrees for quiet, a fan running slow for its own history, a widening hotspot gap, battery and SSD wear milestones, a throttle free month, drift and step changes, and fingerprint results. Rate limited, so it stays observant rather than noisy
- **Report a bug or idea**: a Feedback button in Settings sends your note straight to the developer, with no account and no public thread. Your app version, Windows version, and PC model ride along so a report is reproducible. Nothing else about your machine is sent

## How the score works

DeltaT does not lock a final score on a fixed timer. It calibrates by confidence: it watches how precisely it knows your machine's normal temperature rise at each load level, session by session, and only locks once that reading is statistically solid. Stress the machine with games or heavy work and it locks sooner. Leave it idle and it keeps learning rather than guessing on data it has never seen. CPU and GPU lock independently, each at its own moment of confidence.

While it calibrates it still shows a provisional estimate the moment there is real load to compare, marked with how confident it is so far, so you are never staring at a blank dial. Absolute temperature limit warnings are active from the start regardless. Once the baseline locks, every new reading is compared against your machine's own history, bucketed by load and by outside temperature and corrected for fan speed and power draw, so seasons, fan profiles, and power settings do not create false alarms.

The diagnosis runs on the same evidence as the score, so the two can never disagree. The score tells you how far from normal you are. The diagnosis tells you which part of the cooling system is responsible, and the recommended action follows the cause: a dust driven 25 says clean it out, not repaste it.

## What it does not do

- **No cross machine comparison.** DeltaT never claims "60 C is normal for a 5800X". It tracks drift on your machine against your machine, which is the only honest reference there is
- **Fan normalization is RPM based.** It does not model AIO pump speed, radiator airflow, or case fan layout, so a cooling system change on a custom water loop can look like paste degradation. Hit Recalibrate after any hardware change to the cooling and DeltaT relearns from a clean reference
- **It cannot see inside your case.** Dust on a radiator, a blocked filter, or an intake and exhaust imbalance are inferred from their thermal signature, never observed directly
- **Some laptop sensors are simply hidden.** Battery and board temperatures often sit behind the embedded controller. DeltaT shows them if Windows exposes them and leaves them blank if it does not, never faking a zero
- **HP Omen and Victus, and MSI laptop fan RPM are not supported yet.** Those two only expose fan speed through raw embedded controller access, which is risky to get wrong, so it is deliberately left out rather than shipped unverified. Every other reading DeltaT takes is vendor neutral, so an unsupported chassis loses fan normalization and nothing else
- **Windows only**, by design

## Requirements

- Windows 10 or 11 (64-bit)
- Intel or AMD (Ryzen) CPU, including the newest parts. DeltaT reads the hottest core temperature either vendor exposes, and falls back to the CPU's own thermal registers on chips too new for the standard sensor library, which is what fixes the "my CPU temperature updates once every 20 seconds" problem on the latest Intel laptops
- Administrator rights. CPU temperature registers and storage SMART data require a kernel driver, the same as HWiNFO or HWMonitor. Non elevated runs will miss CPU package temps and drive health

## Install

Run **`DeltaT-Setup-<version>.exe`** and follow the wizard. It is a self contained build, so no separate .NET install is needed on the target machine. The setup:

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

Run the detection benchmark, which is how the accuracy figures above are produced:

```
dotnet run --project src/DeltaT.Spike -- --eval
```

## Package the installer

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Publishes a self-contained `win-x64` build and compiles `installer\DeltaT.iss` into `installer\out\DeltaT-Setup-<version>.exe`. Needs Inno Setup 6 (`winget install --id JRSoftware.InnoSetup`). The version is pulled from `<Version>` in `src/DeltaT.App/DeltaT.App.csproj`, so bump it there when cutting a new release.
