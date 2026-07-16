# Security policy

DeltaT runs with administrator rights and depends on a kernel mode driver to read
your CPU's thermal registers. That combination deserves scrutiny, so if you have
found a way to abuse it, please tell me.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private vulnerability reporting instead: go to the
[Security tab](https://github.com/azmurtaza/DeltaT/security/advisories/new) and
open a draft advisory. It is private between you and me until a fix ships, and it
does not require you to have any prior contact with the project.

Please include what you need to make the problem reproducible: your DeltaT
version, your Windows version, whether PawnIO is installed, and the steps you
took. A proof of concept is welcome but not required if the reasoning is clear.

This is a one person project, so I cannot promise a response time. I will
acknowledge a genuine report as soon as I see it, tell you honestly whether I can
fix it and roughly when, and credit you in the release notes unless you would
rather I did not.

## Supported versions

Only the latest release is supported. DeltaT updates itself on startup, so in
practice almost every install is on the newest version within a day. If you are
reporting a problem, please confirm it is still present on the latest release
first.

## Scope

In scope:

- The DeltaT application and anything it does with its administrator rights.
- The installer, including the step that downloads and verifies the PawnIO setup.
- The auto update path, which downloads and runs a setup file on your machine.
- The in app feedback reporter and the backend it posts to.

Out of scope:

- **PawnIO itself.** It is a separate signed product by a different author.
  Report issues with the driver to
  [PawnIO](https://github.com/namazso/PawnIO.Modules) directly. DeltaT's
  responsibility stops at how it obtains, verifies, and uses it.
- LibreHardwareMonitor, NVIDIA's driver libraries, and vendor firmware
  interfaces, for the same reason. If DeltaT uses one of them unsafely, that
  part is in scope and I want to hear about it.
- Reports that DeltaT requires administrator rights at all, or that it reads
  hardware sensors. Both are the documented purpose of the tool. See the README.
- Findings from an automated scanner with no demonstrated impact.

## Design notes that are already deliberate

Two things look alarming from the outside and are worth stating up front, so you
do not spend time on a false lead.

**DeltaT does not ship WinRing0.** Older versions, up to 2.1.0, reached the CPU
through it. That driver hands any local process arbitrary kernel access
(CVE-2020-14979), which is why Microsoft blocklists it and anti cheats refuse to
run beside it. DeltaT now uses PawnIO, which is signed and executes only verified
modules. Updating removes the old WinRing0 service if a previous version left it
behind.

**The installer never runs a downloaded driver setup on trust.** The PawnIO setup
is fetched from a pinned official release, and its Authenticode signature is
verified before it is executed. If you have found a way around that check, that is
exactly the kind of report I want.

## What DeltaT sends off your machine

For completeness, since it bears on privacy rather than on exploitation:

- Your approximate coordinates go to Open-Meteo for the outside temperature, and
  to BigDataCloud to turn into a place name. That is what makes summer and winter
  readings comparable.
- It checks GitHub for a newer release.
- Nothing else leaves your machine unless you press the feedback button, which
  sends your note plus your app version, Windows version, PC model, CPU, and
  whether you were elevated.

Your temperature history, baseline, and trends stay in a local database on your
own machine and are never uploaded.
