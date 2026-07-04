# DeltaT — verification & how to run

## Running

**Normal (real sensors):** double-click `DeltaT.App.exe`. It relaunches itself elevated
(one UAC prompt) because CPU package temperatures require a kernel driver — the same
requirement as HWiNFO or HWMonitor. Decline the prompt and it still runs, just without
CPU temps (GPU, SSD, battery still work).

**Demo / preview (no admin, no waiting a week):**
```
DeltaT.App.exe --simulate            # healthy machine
DeltaT.App.exe --simulate=aging      # paste starting to go
DeltaT.App.exe --simulate=degraded   # clearly failing paste
DeltaT.App.exe --simulate=dusty      # airflow problem, not paste
```
Simulation uses a separate database (`deltat-sim.db`) so it never touches real history.
Add `--minimized` to start in the tray, `--no-elevate` to skip the elevation relaunch.

## What was verified on the development machine (Acer Nitro V 15, i5-13420H / RTX 3050)

| Check | Result |
|-------|--------|
| Real sensors read elevated | CPU 49–54 °C, GPU 42–43 °C, SSD 47 °C, battery wear 7 %, on-AC detected — matches the P0 spike dump |
| Ambient correction | Live weather resolved by IP (Open-Meteo), stored per-sample; Δ-over-outside shown on every card |
| Model profile fallback | Nitro ANV15-51 → exact `acer-nitro-v15` profile (TjMax 100 °C read from silicon, GPU limit 87 °C) |
| CPU minimized (tray only) | **0.064 %** average (budget < 0.5 %) |
| CPU window visible, rendering | **0.143 %** average |
| RAM minimized private / working set | **54 MB / 118 MB** (budget < 150 MB) |
| Unit tests | 49 / 49 passing (`dotnet test`) |
| Single-file publish | 70.5 MB self-contained exe, launches clean |
| All five screens | Dashboard, Trends, Remarks, Settings, Onboarding + Fingerprint dialog render and navigate |

## Reproducing the checks

```powershell
dotnet test                                    # 49 unit tests
dotnet run --project src/DeltaT.Spike           # raw sensor dump (run elevated for CPU temps)
dotnet run --project src/DeltaT.App -- --simulate=degraded   # preview a failing-paste dashboard
```

Perf (while a build is running / minimized):
```powershell
$p = Get-Process DeltaT.App
$c1=$p.TotalProcessorTime.TotalMilliseconds; sleep 45; $p.Refresh()
"CPU {0:N3}%" -f (($p.TotalProcessorTime.TotalMilliseconds-$c1)/45000/[Environment]::ProcessorCount*100)
"RAM private {0:N0} MB" -f ($p.PrivateMemorySize64/1MB)
```

## Known hardware limits on this machine (expected, not bugs)

- **Battery temperature** is not exposed by the Nitro's ACPI — the battery card shows wear
  and charge only, no temp ring. (This is normal; most laptops hide battery temp.)
- **Fan RPM and a board/system temperature** aren't exposed either — the EC keeps them
  private. CPU / GPU / SSD (the paste-relevant ones) all read reliably.
- **The paste verdict needs ~7 days** of normal use before it stops saying "calibrating."
  Absolute-limit warnings and throttle detection work from the first minute. Use
  `--simulate=degraded` to see a locked verdict immediately.
