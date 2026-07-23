# DeltaT Privacy Policy

*Effective 2026-07-22*

In short: almost nothing leaves your machine. This covers exactly what's
collected, when, and why, in the same plain terms as the app itself.

## By default: nothing

DeltaT is a local thermal diagnostician. It has no account, no login, and no
analytics SDK. Every sensor reading, learned baseline, score, and history
entry is written to a SQLite database at `%LOCALAPPDATA%\DeltaT` on your own
machine and never transmitted anywhere.

Nothing you don't take action on is ever sent off your device.

## When you submit feedback or an idea

The one place DeltaT talks to a server the developer controls is the
feedback form (Settings → Feedback). Submitting a report sends:

- **Your message** (sent) - whether it's a bug or an idea, and what you wrote.
- **Contact info** (sent, optional) - only if you choose to leave one, so the
  developer can follow up.
- **App & machine context** (sent automatically) - app version, OS version,
  machine name, CPU model, GPU model(s), and whether DeltaT is running
  elevated, so the report can be triaged without asking you for it
  separately.

Nothing else on your machine (no sensor history, no scores, no location) is
included in a feedback report. Reports are stored in a locked-down database
with no public read or write access; only the developer's backend functions
can see them.

## Services the app talks to directly

A few features need to reach a third party to work at all. These calls go
straight from your machine to that service; the developer never sees them:

**Weather & location** (not seen by developer)
Ambient-temperature correction resolves your position via Windows location
services, then queries Open-Meteo for weather and (optionally) BigDataCloud
for a place name to display. Used only to correct thermal readings for
outdoor temperature; you can pin a fixed indoor temperature instead in
Settings to skip this entirely.

**Update check** (not seen by developer)
On startup DeltaT asks GitHub's public releases API whether a newer version
exists. No machine or personal data is sent; it's the same request anyone's
browser makes visiting the releases page. Turn off in Settings → Updates if
you'd rather update manually.

## What DeltaT never does

- No background telemetry, usage analytics, or crash reporting.
- No selling, sharing, or monetizing of any data.
- No tracking of tip-jar/donation activity; those pages just display static
  payment details.
- No cross-machine or cloud sync of your thermal history; it stays on the
  machine that recorded it.

## Contact

Questions about this policy, or a request to delete a submitted feedback
report, can be sent via the contact info in the feedback form, or by opening
an issue at [github.com/deltat-app/DeltaT](https://github.com/deltat-app/DeltaT).
