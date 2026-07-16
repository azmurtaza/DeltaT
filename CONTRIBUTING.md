# Contributing to DeltaT

DeltaT is a one person project. That shapes what is realistic here, so this page
is short and honest rather than aspirational.

## Before you write code, open an issue

Please do not send a large pull request out of the blue. I would rather talk about
the idea first than have you spend an evening on something I cannot merge. A small
obvious fix (a typo, a crash with a clear cause, a broken link) can go straight to
a pull request.

The single most useful thing you can contribute is not code at all. It is a bug
report from hardware I do not own. See below.

## The one rule that is not negotiable: accuracy is measured, not argued

DeltaT's whole claim is that it can tell you what is actually driving your
temperatures. That claim is backed by a benchmark, not by intuition, and the
numbers are guarded by tests that fail the build if they regress.

If your change touches the score, the baseline, the diagnosis, or the telemetry
that feeds them, it has to either raise those numbers or hold them steady. A
feature is not worth a point of accuracy. Run it:

```
dotnet run --project src/DeltaT.Spike -- --eval
```

Current numbers, which your change should land within noise of: roughly 100
percent fault detection, 99.8 percent confounder clear rate, 99.9 percent overall
cause attribution.

If the benchmark cannot see the failure mode your change is about, then extend the
benchmark first so the claim is measured, and lock the new floor with a test. That
ordering is the point. A change that is only argued to be better is not shippable,
however sensible the argument sounds.

## Build and test

You need the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`). Windows only,
by design, because the sensor layer is.

```
dotnet build DeltaT.sln
dotnet test
dotnet run --project src/DeltaT.App
```

The app needs administrator rights for CPU temperatures and drive SMART. A non
elevated run works but is missing those readings, which is usually the cause of a
confusing local result.

You do not need real hardware to work on most of it. `--simulate` substitutes a
fake sensor source:

```
dotnet run --project src/DeltaT.App -- --simulate
```

And you can check UI changes without a human looking at them:

```
dotnet run --project src/DeltaT.App -- --simulate --uishot=DIR
```

## Reports from hardware I do not have are the best contribution

Fan RPM is the one reading that depends on your laptop's vendor. Acer Nitro and
Predator are verified on real hardware. Lenovo Legion and LOQ, ASUS ROG, TUF and
Zenbook, HP business laptops, and HP OMEN and Victus are all implemented from
public protocol documentation but have **never been confirmed on a real machine**.
They fail safe, so a wrong guess goes dark rather than reporting a wrong number,
but that means I cannot tell the difference between "unsupported" and "broken"
without you.

If you have one of those machines, run this from an elevated terminal and send me
what it prints:

```
dotnet run --project src/DeltaT.Spike -- --fans
```

MSI is not supported at all yet, and needs a different approach again.

## Code shape

- `src/DeltaT.Core` holds sensors, storage, scoring, and diagnosis. It has no UI
  dependencies. Keep it that way.
- `src/DeltaT.App` is the WPF UI. Follow `docs/DESIGN.md` and the standing rules in
  `docs/DESIGN-AUDIT.md`, which new UI is expected to pass.
- `tests/DeltaT.Core.Tests` is xunit.

A few invariants worth knowing before you trip over them:

- The scoring engine is pure. No clocks, no I/O, no sensor calls inside it.
  Snapshots and baselines go in, a score comes out. Learning window logic takes
  timestamps explicitly. This is what makes the benchmark possible.
- Temperatures are always Celsius internally, converted only at the display edge.
  Timestamps are always UTC, converted to local only at the display edge.
- All sensor access goes through `ISensorSource`, so the simulated source can
  stand in for hardware.
- Never fake a reading. A value the hardware cannot measure shows as `--`, never
  as a zero and never as a plausible looking guess.

## Licensing

DeltaT is GPL-3.0. By contributing you agree that your contribution ships under
the same license.
