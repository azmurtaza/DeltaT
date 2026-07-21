using DeltaT.Core.Machine;
using LibreHardwareMonitor.Hardware.Motherboard.Lpc.EC;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads CPU and GPU fan RPM on HP's consumer gaming line (OMEN / Victus) straight
/// from the Embedded Controller. Unlike Acer/Lenovo/ASUS/HP-business, these chassis expose no
/// WMI getter for measured RPM: HP's BIOS-WMI interface only reports fan *level/table*, so the
/// live tachometer is EC-only. The register map (OmenMon's, and what the open-source OmenCore
/// reads too) is a 16-bit word per fan at 0xB0 (fan 1) and 0xB2 (fan 2).
///
/// Reaching the EC used to mean raw port I/O through a kernel primitive like WinRing0, which is
/// exactly what DeltaT dropped for security (anti-cheat blocks it). This reader instead goes
/// through the LibreHardwareMonitor 0.9.6 <see cref="WindowsEmbeddedControllerIO"/>, which
/// performs the 0x62/0x66 command/data handshake inside a **signed PawnIO module**
/// (LpcACPIEC / IsaBridgeEC) and, critically, takes the ACPI <c>Global\Access_EC</c> mutex the
/// OS and other apps (OMEN Gaming Hub, FanControl) also honour. So a read is arbitrated, never
/// a bare port poke: when the firmware or another app holds the EC it throws
/// (BusMutexLockingFailedException) rather than colliding, which is the classic cause of an
/// EC lockup. This reader treats any such failure as a miss and backs off; it never retries in
/// a tight loop, and it never writes (no fan curve, profile or LED is ever touched).
///
/// Safety gating, strongest first, so this only ever touches an EC we understand:
///  - PawnIO must be installed (no signed EC module ⇒ no EC access is attempted at all).
///  - The machine must identify as an HP OMEN or Victus (an HP register map is never issued to
///    another vendor's EC). A business HP latches earlier through <see cref="HpWmiFanReader"/>.
///  - Readings must decode as a plausible fan speed; anything else and the probe goes dark,
///    leaving RPM null ("--" upstream, never a faked 0).
///
/// Not hardware-verified yet: the map is from OmenMon and the endianness of the word is the one
/// open question (this project's notes have carried both), so <see cref="DecodeFan"/> accepts
/// whichever byte order yields a believable RPM and the <c>--omen</c> diagnostic prints both for
/// a real machine to settle. Not thread-safe by design: called from the monitor thread on the
/// slow (6 s) vendor-fan clock, like the LHM session it rides along with.</summary>
public sealed class HpOmenEcFanReader : ILaptopFanProbe
{
    public string Vendor => "HP Omen";

    /// <summary>Ruled out once we know this isn't a PawnIO-equipped OMEN/Victus, or after a run
    /// of EC reads that never once decoded a real fan (a model whose map differs). A parked fan
    /// reads as all-zero, which is a valid quiet state, not a miss, so an idle machine never
    /// trips this before its fans first spin.</summary>
    public bool IsDead => _dead;

    // EC register map (OmenMon): a 16-bit word per fan. Fan 1 low/high at 0xB0/0xB1,
    // fan 2 low/high at 0xB2/0xB3. Which fan is CPU vs GPU is confirmed by the diagnostic;
    // on these chassis fan 1 tracks the CPU side, fan 2 the GPU side.
    private static readonly ushort[] FanRegisters = { 0xB0, 0xB1, 0xB2, 0xB3 };

    // A spinning laptop fan sits well inside this band; the floor rejects a lone low byte read
    // as noise, the ceiling rejects a byte-order or wrong-register misread AND the EC's torn
    // reads. The EC updates its 16-bit fan word non-atomically, so a poll that lands mid-update
    // catches a stale/zero byte beside a fresh one: a low byte of 0x00 next to a high byte near
    // 0x2A decodes little-endian to ~10,700, which is the bogus "~11000 rpm" spike users saw.
    // Omen/Victus blower fans top out near 6000, so an 8000 ceiling clears any real speed with
    // margin while rejecting that whole family of torn values. Zero is handled separately as
    // "parked", not as an out-of-range value.
    private const int MinSpinRpm = 200;
    private const int MaxSpinRpm = 8000;

    // An OMEN/Victus that never yields a plausible fan after this many non-parked reads has a
    // register map we don't fit — stop polling its EC. Never trips once a real fan has decoded.
    private const int GiveUpAfterMisses = 30;

    public enum FanDecode { Parked, Rpm, Implausible }

    // How often to re-check whether OmenMon has appeared since we opened the EC. OmenMon can be
    // started after DeltaT; when it is, we release the EC session so its readings recover.
    private static readonly TimeSpan ConflictRecheck = TimeSpan.FromSeconds(15);

    private readonly Func<MachineIdentity> _identify;
    private readonly Func<bool> _ecConflictPresent;
    private WindowsEmbeddedControllerIO? _ec;
    private bool _initTried;
    private bool _dead;
    private bool _everDecoded;
    private int _consecutiveMisses;
    private DateTimeOffset _lastConflictCheck = DateTimeOffset.MinValue;
    // The EC's byte order for a fan word, latched on the first believable decode. Both fans
    // share one EC so one latch serves both. Once known we trust ONLY that order: accepting
    // either order on every tick is a spike vector, because a torn read that is implausible in
    // the true order can still look plausible in the other and slip through as a bogus RPM.
    private bool? _bigEndian;

    public HpOmenEcFanReader() : this(MachineIdentityProvider.Detect) { }

    /// <summary>Test/diagnostic seam: supply the machine identity (and optionally the EC-conflict
    /// probe) instead of scanning WMI and the process list.</summary>
    /// <param name="ecConflictPresent">Returns true when another app that owns the same PawnIO EC
    /// session is running (OmenMon). When it is, this reader yields the EC entirely rather than
    /// colliding: two processes on the one EC module knock each other's readings out (issue #1).
    /// Defaults to a scan for an OmenMon process.</param>
    public HpOmenEcFanReader(Func<MachineIdentity> identify, Func<bool>? ecConflictPresent = null)
    {
        _identify = identify;
        _ecConflictPresent = ecConflictPresent ?? OmenMonProcessRunning;
    }

    public LaptopFanSample Read()
    {
        if (_dead)
            return default;
        if (!_initTried)
            Init();
        if (_ec is null)
            return default;

        // OmenMon may have launched after we opened the EC. Re-check occasionally (a process scan
        // is not free) and, if it is now running, hand the EC session back to it: keeping it would
        // leave OmenMon blind. Our fans go to "--" until DeltaT is restarted without OmenMon, which
        // is the right trade, OmenMon is the user's fan controller and DeltaT only reads.
        if (DateTimeOffset.UtcNow - _lastConflictCheck >= ConflictRecheck)
        {
            _lastConflictCheck = DateTimeOffset.UtcNow;
            if (_ecConflictPresent())
            {
                Dispose();
                return default;
            }
        }

        var data = new byte[FanRegisters.Length];
        try
        {
            // One arbitrated batch read: the EC bus mutex is taken once for all four registers,
            // minimising the window we hold it. Read-only.
            _ec.Read(FanRegisters, data);
        }
        catch
        {
            // EC busy (mutex held by the firmware or another app) or any EC error. Never rethrow,
            // never hammer: count a miss and, if we have never once decoded a real fan, give up so
            // we stop contending for the EC on a machine this reader can't serve.
            RegisterMiss();
            return default;
        }

        FanDecode cpuKind = DecodeFan(data[0], data[1], _bigEndian, out double cpuRpm, out bool cpuBig);
        FanDecode gpuKind = DecodeFan(data[2], data[3], _bigEndian, out double gpuRpm, out bool gpuBig);

        if (cpuKind == FanDecode.Rpm || gpuKind == FanDecode.Rpm)
        {
            _everDecoded = true;
            _consecutiveMisses = 0;
            // Latch the byte order the first real decode used, so every later tick trusts only it.
            _bigEndian ??= cpuKind == FanDecode.Rpm ? cpuBig : gpuBig;
        }
        else if (cpuKind == FanDecode.Implausible || gpuKind == FanDecode.Implausible)
        {
            // Non-zero bytes that don't read as a believable RPM: wrong register map or wrong
            // machine. Parked (all-zero) does NOT land here, so a quiet idle keeps its turn.
            RegisterMiss();
        }

        return new LaptopFanSample(ResolveFan(cpuKind, cpuRpm), ResolveFan(gpuKind, gpuRpm));
    }

    /// <summary>Turn a decode into the reported RPM. A believable reading is itself; a parked
    /// (all-zero) fan reads as a real 0 <em>once we've proven these fans exist</em> (any earlier
    /// spin decoded), because then a genuine 0x0000 is the fan measurably stopped, not an absent
    /// sensor, so it shows "0 rpm" instead of "--". Before that first spin, and for an implausible
    /// read, it stays null: we can't yet claim a fan is there to be off.</summary>
    private double? ResolveFan(FanDecode kind, double rpm) => kind switch
    {
        FanDecode.Rpm => rpm,
        FanDecode.Parked when _everDecoded => 0.0,
        _ => null,
    };

    private void RegisterMiss()
    {
        if (++_consecutiveMisses >= GiveUpAfterMisses && !_everDecoded)
            _dead = true;
    }

    private void Init()
    {
        _initTried = true;
        try
        {
            if (!PawnIoStatus.IsInstalled)
            {
                // No signed EC module on this machine: never fall back to a bare port poke.
                _dead = true;
                return;
            }
            if (!IsHpOmenOrVictus(_identify()))
            {
                // Not our hardware: an HP register map is never issued to another vendor's EC.
                _dead = true;
                return;
            }
            if (_ecConflictPresent())
            {
                // OmenMon is already running and owns the EC. Do not open our own session: two
                // processes on the same PawnIO EC module knock each other out (issue #1). Yield
                // it entirely. The OmenMon pipe reader auditions ahead of us, so if OmenMon is
                // publishing its fan RPM DeltaT still gets full fan telemetry without the EC.
                _dead = true;
                return;
            }
            _lastConflictCheck = DateTimeOffset.UtcNow;
            _ec = new WindowsEmbeddedControllerIO();
        }
        catch
        {
            // PawnIO present but the EC module won't bind (non-elevated, unsupported): stay dark.
            _ec = null;
        }
        if (_ec is null)
            _dead = true;
    }

    /// <summary>Is an OmenMon (or OmenMon Reborn) process running? It owns the same PawnIO EC
    /// module, so DeltaT must not open the EC alongside it. A best-effort scan: any failure reads
    /// as "not present" so a WMI/permissions hiccup never wrongly blinds DeltaT's own fan reading.</summary>
    private static bool OmenMonProcessRunning()
    {
        try
        {
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    if (p.ProcessName.Contains("omenmon", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { /* process enumeration blocked: assume no conflict rather than blind ourselves */ }
        return false;
    }

    /// <summary>True only for an HP OMEN or Victus. The manufacturer gate keeps the EC map off
    /// every other vendor; the model/family gate keeps it off HP's business machines (which read
    /// their fans through <see cref="HpWmiFanReader"/> instead).</summary>
    public static bool IsHpOmenOrVictus(MachineIdentity id)
    {
        bool isHp = id.Manufacturer.Contains("HP", StringComparison.OrdinalIgnoreCase)
                    || id.Manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase);
        if (!isHp)
            return false;
        string haystack = $"{id.Model} {id.SystemFamily}";
        return haystack.Contains("OMEN", StringComparison.OrdinalIgnoreCase)
               || haystack.Contains("Victus", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pure protocol half, split out for tests. Two consecutive EC bytes are a fan-speed
    /// word. All-zero is a parked (or not-yet-spun) fan, a valid quiet state. With the byte order
    /// still unknown the little-endian order is tried first (OmenMon's documented layout);
    /// big-endian is accepted only if it alone reads as a believable RPM, so the reader survives
    /// the unconfirmed endianness until a real machine settles it. A non-zero pair that is
    /// plausible in neither order is reported <see cref="FanDecode.Implausible"/> (wrong map /
    /// wrong machine).</summary>
    public static FanDecode DecodeFan(byte b0, byte b1, out double rpm)
        => DecodeFan(b0, b1, bigEndian: null, out rpm, out _);

    /// <summary>As above, but once the machine's byte order is known (<paramref name="bigEndian"/>
    /// non-null) only that order is tried, so a torn read that is implausible in the true order
    /// can no longer be rescued by the other order into a bogus spike. <paramref name="usedBigEndian"/>
    /// reports which order produced the reading, so the caller can latch it after the first spin.</summary>
    public static FanDecode DecodeFan(byte b0, byte b1, bool? bigEndian, out double rpm, out bool usedBigEndian)
    {
        rpm = 0;
        usedBigEndian = bigEndian ?? false;
        if (b0 == 0 && b1 == 0)
            return FanDecode.Parked;

        int littleEndian = b0 | (b1 << 8);
        int bigEndianVal = b1 | (b0 << 8);

        if (bigEndian == true)
            return Plausible(bigEndianVal, out rpm);
        if (bigEndian == false)
            return Plausible(littleEndian, out rpm);

        if (IsPlausible(littleEndian))
        {
            rpm = littleEndian;
            usedBigEndian = false;
            return FanDecode.Rpm;
        }
        if (IsPlausible(bigEndianVal))
        {
            rpm = bigEndianVal;
            usedBigEndian = true;
            return FanDecode.Rpm;
        }
        return FanDecode.Implausible;
    }

    private static FanDecode Plausible(int candidate, out double rpm)
    {
        if (IsPlausible(candidate))
        {
            rpm = candidate;
            return FanDecode.Rpm;
        }
        rpm = 0;
        return FanDecode.Implausible;
    }

    private static bool IsPlausible(int rpm) => rpm is >= MinSpinRpm and <= MaxSpinRpm;

    public void Dispose()
    {
        try { _ec?.Dispose(); } catch { /* EC handle already gone */ }
        _ec = null;
        _dead = true;
    }
}
