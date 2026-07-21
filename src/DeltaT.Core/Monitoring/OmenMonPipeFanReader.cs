using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using DeltaT.Core.Machine;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads CPU/GPU fan RPM from OmenMon (and OmenMon Reborn) over its named pipe,
/// instead of touching the Embedded Controller ourselves.
///
/// Why this exists: on HP OMEN/Victus the live fan tachometer is EC-only, and both DeltaT and
/// OmenMon reach it through the same signed PawnIO EC module. Opening that path from two
/// processes is a driver-level session conflict, not just mutex contention: the moment DeltaT
/// starts, OmenMon loses every reading (GitHub issue #1). OmenMon is a fan-CONTROL tool people
/// keep running; DeltaT only needs to READ the fans. So when OmenMon is present, the right move
/// is to let it own the EC and take the numbers it already has: OmenMon publishes them on the
/// pipe <c>\\.\pipe\OmenMon_FanData</c>, and this probe reads them there. No EC session is
/// opened, so nothing collides.
///
/// It auditions AHEAD of <see cref="HpOmenEcFanReader"/>: if the pipe answers, the EC reader is
/// never reached, so DeltaT keeps full fan telemetry while OmenMon keeps working. If the pipe
/// is absent (OmenMon not running, or a build that doesn't publish it) this stays quiet and the
/// EC reader takes over as before. It never marks itself dead on a missing pipe, because OmenMon
/// can be started at any time after DeltaT.
///
/// Protocol (documented for the OmenMon side to match): the server accepts a client on the pipe
/// and writes one UTF-8 text line per update, a JSON object <c>{"cpu":4022,"gpu":3623}</c> where
/// the values are RPM. Missing/unknown fans may be omitted or sent as null. <see cref="ParseFanData"/>
/// is deliberately lenient (it also accepts <c>cpuFan</c>/<c>gpuFan</c> spellings and
/// <c>key=value</c>) so a small formatting difference on the publisher side still reads.
/// Strictly read-only: it only connects and reads; it never writes to the pipe.</summary>
public sealed class OmenMonPipeFanReader : ILaptopFanProbe
{
    public const string PipeName = "OmenMon_FanData";

    // Short so an absent server can't stall the audition, and so once latched the 6 s fan clock
    // is never held up: a live OmenMon writes continuously, so a connect and one line return
    // well inside this. A slow or silent server just yields "no reading this tick".
    private const int ConnectTimeoutMs = 150;

    public string Vendor => "OmenMon";

    private readonly Func<MachineIdentity> _identify;
    private bool _initTried;
    private bool _dead;

    public OmenMonPipeFanReader() : this(MachineIdentityProvider.Detect) { }

    /// <summary>Test seam: supply the machine identity instead of probing WMI.</summary>
    public OmenMonPipeFanReader(Func<MachineIdentity> identify) => _identify = identify;

    /// <summary>Ruled out only on hardware OmenMon never runs on (not HP OMEN/Victus). A missing
    /// pipe is NOT death: OmenMon may launch after DeltaT, and this probe must be ready for it.</summary>
    public bool IsDead => _dead;

    public LaptopFanSample Read()
    {
        if (_dead)
            return default;
        if (!_initTried)
        {
            _initTried = true;
            // OmenMon only exists on HP's OMEN/Victus line; on anything else the pipe will never
            // appear, so stay dark instead of connecting into the void every tick.
            if (!HpOmenEcFanReader.IsHpOmenOrVictus(SafeIdentify()))
            {
                _dead = true;
                return default;
            }
        }

        string? line = TryReadLine();
        return line is null ? default : ParseFanData(line);
    }

    private MachineIdentity SafeIdentify()
    {
        try { return _identify(); }
        catch { return new MachineIdentity("", "", "", false, "", Array.Empty<string>()); }
    }

    private static string? TryReadLine()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.In);
            client.Connect(ConnectTimeoutMs); // throws promptly when no server is listening
            using var reader = new StreamReader(client, Encoding.UTF8);
            return reader.ReadLine();
        }
        catch
        {
            // No server, a torn connection, or a read error: no reading this tick. Never rethrow
            // onto the monitor thread, never retry in a tight loop (the 6 s clock paces us).
            return default;
        }
    }

    private static readonly Regex CpuRe = new(@"(?:cpu(?:fan)?)\D{0,4}(\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GpuRe = new(@"(?:gpu(?:fan)?)\D{0,4}(\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Pure protocol half, split out for tests. Pulls the CPU and GPU RPM out of one
    /// published line. Lenient by design: the canonical form is JSON
    /// <c>{"cpu":4022,"gpu":3623}</c>, but <c>cpuFan</c>/<c>gpuFan</c> keys and <c>=</c>
    /// separators read too, so a minor difference on the publisher side is tolerated rather than
    /// silently dropping the whole reading. A fan that isn't mentioned comes back null ("--"
    /// upstream, never a faked 0); a mentioned 0 is a genuine parked fan and passes through.</summary>
    public static LaptopFanSample ParseFanData(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return default;
        double? cpu = Match(CpuRe, line);
        double? gpu = Match(GpuRe, line);
        return new LaptopFanSample(cpu, gpu);

        static double? Match(Regex re, string s) =>
            re.Match(s) is { Success: true } m && double.TryParse(m.Groups[1].Value, out double v) ? v : null;
    }

    public void Dispose() => _dead = true;
}
