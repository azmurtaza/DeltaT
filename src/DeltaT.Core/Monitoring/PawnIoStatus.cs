using LibreHardwareMonitor.PawnIo;

namespace DeltaT.Core.Monitoring;

/// <summary>Is the kernel driver DeltaT reads the CPU through actually present?
///
/// LibreHardwareMonitor 0.9.5+ carries no driver of its own: it talks to **PawnIO**, a
/// separately installed, signed driver that runs only verified modules. That replaced
/// WinRing0, whose ioctl exposed arbitrary MSR / port / physical-memory access to any local
/// process (CVE-2020-14979) — which is why Microsoft blocklists it, Defender flags it as
/// HackTool:Win32/Winring0, and kernel anti-cheats (EA Javelin in Battlefield 6, EAC,
/// Vanguard) refuse to start beside it. The trade is that PawnIO is a hard runtime
/// dependency: without it there is no ring0 access, so no CPU die temperature, no TjMax, no
/// throttle bit and no package power.
///
/// DeltaT's installer chains PawnIO's own installer, so most users never see this. But a
/// user who declined it, an xcopy deployment, or a Windows update that removed the driver
/// all land here — and a monitoring tool that silently shows blank temperatures in that case
/// is worse than one that says why. Everything else DeltaT reads is driver-free (GPU via
/// NVML, fan RPM via vendor WMI, SSD via SMART, battery via ACPI), so the app still runs;
/// it just loses the CPU signals that need the silicon.</summary>
public static class PawnIoStatus
{
    /// <summary>True when the PawnIO driver is installed and usable for kernel reads.</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return PawnIo.IsInstalled; }
            catch { return false; }
        }
    }

    /// <summary>Installed PawnIO version, or null when it isn't present.</summary>
    public static Version? Version
    {
        get
        {
            try { return IsInstalled ? PawnIo.Version : null; }
            catch { return null; }
        }
    }

    /// <summary>Where a user is sent to install it. The signed "Official edition" is the one
    /// to take: the "Unrestricted" build disables module signature checking, which throws away
    /// the property that makes PawnIO safe (and unsigned, it would be flagged all over again).</summary>
    public const string DownloadUrl = "https://pawnio.eu";

    /// <summary>Plain-English explanation for the UI when the driver is missing.</summary>
    public const string MissingMessage =
        "The PawnIO sensor driver isn't installed, so DeltaT can't read the CPU's own thermal registers. "
        + "CPU temperature falls back to a slower, coarser motherboard reading, and package power, throttle "
        + "events and headroom are unavailable. Everything else (GPU, fans, drive, battery) is unaffected.";
}
