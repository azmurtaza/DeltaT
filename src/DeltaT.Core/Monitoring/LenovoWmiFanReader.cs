using System.Management;

namespace DeltaT.Core.Monitoring;

/// <summary>Reads CPU and GPU fan RPM from Lenovo's gaming firmware WMI interfaces on Legion
/// and LOQ machines — the same channels Lenovo Vantage / Legion Toolkit poll. Laptop fans live
/// behind the EC where LibreHardwareMonitor sees nothing, so on these machines this is the only
/// fan telemetry there is, and fan RPM is what lets scoring separate "genuinely cooler" from
/// "fans cranked".
///
/// <b>Three firmware generations, three protocols.</b> Lenovo has moved the fan getter twice and
/// left the superseded ones in place as stubs, which is the trap here: on a Gen 9 machine
/// <c>LENOVO_GAMEZONE_DATA.GetFan1Speed</c> still exists and still answers, it just returns 0
/// forever (measured on a Legion 7i Gen 9 16IRX9, 2026-07-23: <c>GetCPUTemp</c> tracked a real
/// load 61 → 97 °C while every fan getter, including the constant <c>GetFanCount</c>, stayed 0
/// with the fans audibly spinning). A stubbed 0 is indistinguishable from a parked fan on an idle
/// machine, so betting on one protocol silently yields no fan data on the generations that
/// stubbed it. Instead all three are tried in newest-first order and the one that returns a real
/// nonzero RPM is latched:
/// <list type="number">
/// <item><c>LENOVO_OTHER_METHOD.GetFeatureValue(IDs)</c> → <c>Value</c>, ids 0x04030001 (CPU) and
/// 0x04030002 (GPU). Gen 9 and newer.</item>
/// <item><c>LENOVO_GAMEZONE_DATA.GetFan1Speed()</c> / <c>GetFan2Speed()</c> → <c>Data</c>,
/// parameterless. Mid-generation.</item>
/// <item><c>LENOVO_FAN_METHOD.Fan_GetCurrentFanSpeed(FanID)</c> → <c>CurrentFanSpeed</c>, with
/// <b>zero-based</b> ids (0 = CPU, 1 = GPU). Older machines.</item>
/// </list>
/// Protocols and ids taken from LenovoLegionToolkit's three sensor controllers (V3, V1 and V2
/// respectively), which is the reference implementation for all of them.
///
/// Strictly read-only: every method invoked is a getter, never a Set* or <c>Fan_Set_Table</c>, so
/// fan curves, modes and OC state are untouched. Self-gating mirrors <see cref="AcerWmiFanReader"/>:
/// at least one class must exist (Legion/LOQ firmware), the process must be elevated (root\WMI
/// denies standard users), and a method must actually answer. Because a parked fan and a stubbed
/// getter both read 0, a protocol is only latched on a nonzero reading; until then every protocol
/// is retried each tick, so a quiet machine simply reports no fan data rather than locking onto a
/// dead interface. Not thread-safe by design: call it from the monitor thread.</summary>
public sealed class LenovoWmiFanReader : ILaptopFanProbe
{
    /// <summary>One firmware generation's fan-getter protocol. Attribution (which physical fan
    /// cools which part) can vary by model, but both feed the same chassis-fan proxy downstream,
    /// so a swap is harmless.</summary>
    private readonly record struct Protocol(
        string ClassName,
        string CpuMethod,
        string GpuMethod,
        string OutProperty,
        string? ParamName,
        int CpuArg,
        int GpuArg);

    // Newest first: a Gen 9 machine answers 0 on the two older interfaces, so trying them first
    // would read as "fans parked" forever.
    private static readonly Protocol[] Protocols =
    {
        new("LENOVO_OTHER_METHOD", "GetFeatureValue", "GetFeatureValue", "Value", "IDs", 0x04030001, 0x04030002),
        new("LENOVO_GAMEZONE_DATA", "GetFan1Speed", "GetFan2Speed", "Data", null, 0, 0),
        new("LENOVO_FAN_METHOD", "Fan_GetCurrentFanSpeed", "Fan_GetCurrentFanSpeed", "CurrentFanSpeed", "FanID", 0, 1),
    };

    // A firmware exposing a class but never answering a valid reading is a non-gaming variant —
    // stop asking. Once one valid answer lands the reader never self-disables on quiet idle or a
    // transient EC hiccup.
    private const int GiveUpAfterMisses = 30;

    public string Vendor => "Lenovo";
    public bool IsDead => _dead;

    private readonly Dictionary<string, ManagementObject> _instances = new();
    private Protocol? _latched;
    private bool _initTried;
    private bool _dead;
    private bool _everAnswered;
    private int _consecutiveMisses;

    public LaptopFanSample Read()
    {
        if (_dead)
            return default;
        if (!_initTried)
            Init();
        if (_instances.Count == 0)
            return default;

        bool anyAnswered = false;

        // Once a protocol has proven itself with a real reading, it owns this machine.
        if (_latched is { } latched)
        {
            LaptopFanSample settled = ReadWith(latched, ref anyAnswered);
            Account(anyAnswered);
            return settled;
        }

        // Still auditioning: the first protocol to report real airflow wins. A protocol that
        // answers only zeros (parked fans, or a stubbed getter) does not latch, so the next tick
        // gets to try them all again.
        foreach (Protocol p in Protocols)
        {
            if (!_instances.ContainsKey(p.ClassName))
                continue;
            LaptopFanSample sample = ReadWith(p, ref anyAnswered);
            if (sample.HasAny)
            {
                _latched = p;
                Account(true);
                return sample;
            }
        }

        Account(anyAnswered);
        return default;
    }

    /// <summary>Track whether this firmware ever answers at all, so a class that exists but never
    /// responds eventually stops being polled.</summary>
    private void Account(bool answered)
    {
        if (answered)
        {
            _everAnswered = true;
            _consecutiveMisses = 0;
        }
        else if (++_consecutiveMisses >= GiveUpAfterMisses && !_everAnswered)
        {
            _dead = true;
        }
    }

    private void Init()
    {
        _initTried = true;
        foreach (Protocol p in Protocols)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", $"SELECT * FROM {p.ClassName}");
                foreach (ManagementBaseObject mo in searcher.Get())
                {
                    _instances[p.ClassName] = (ManagementObject)mo;
                    break;
                }
            }
            catch
            {
                // Not Legion/LOQ firmware / not elevated / this generation lacks the class.
            }
        }
        _dead = _instances.Count == 0;
    }

    private LaptopFanSample ReadWith(Protocol p, ref bool anyAnswered)
    {
        ManagementObject instance = _instances[p.ClassName];
        double? cpu = ReadRpm(instance, p, p.CpuMethod, p.CpuArg, ref anyAnswered);
        double? gpu = ReadRpm(instance, p, p.GpuMethod, p.GpuArg, ref anyAnswered);
        return new LaptopFanSample(cpu, gpu);
    }

    private static double? ReadRpm(
        ManagementObject instance, Protocol p, string method, int arg, ref bool anyAnswered)
    {
        try
        {
            ManagementBaseObject? inParams = null;
            if (p.ParamName is { } param)
            {
                inParams = instance.GetMethodParameters(method);
                inParams[param] = arg;
            }

            using (inParams)
            using (ManagementBaseObject? outParams = instance.InvokeMethod(method, inParams, null))
            {
                if (outParams?[p.OutProperty] is not { } raw)
                    return null;
                // The method answered — that alone proves this is the right vendor, even if the
                // fan is parked (0) or the value is out of range.
                anyAnswered = true;
                return PlausibleRpm(Convert.ToInt32(raw));
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A parked fan (0) and any out-of-range reading map to null — a real answer, but not
    /// airflow data, exactly like every other absent reading here. Zero is also what a superseded
    /// firmware stub returns, which is precisely why it must never latch a protocol.</summary>
    public static double? PlausibleRpm(int rpm) => rpm is > 0 and < 10000 ? rpm : null;

    public void Dispose()
    {
        foreach (ManagementObject instance in _instances.Values)
            instance.Dispose();
        _instances.Clear();
        _dead = true;
    }
}
