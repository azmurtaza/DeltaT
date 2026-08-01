using System.Globalization;
using DeltaT.Core.Storage;

namespace DeltaT.Core.Scoring;

/// <summary>One user-declared cooling setup: a laptop cooler pad's fan turned up or down, a
/// case side panel on or off, a chassis fan profile. Id 0 is the implicit default every
/// existing install already has; ids are assigned monotonically and never reused, because a
/// reused id would inherit the deleted setup's learned rows.</summary>
public sealed record CoolingProfile(int Id, string Name)
{
    public const int DefaultId = 0;
    public const string DefaultName = "Default";

    /// <summary>Longest name the settings encoding round-trips safely, and about as much as
    /// the tray menu and the Settings row can show.</summary>
    public const int MaxNameLength = 28;
}

/// <summary>Which incomparable reference regime a learned row belongs to.
///
/// <para>DeltaT already had one axis of this: the ambient source (0 = outside weather,
/// 1 = a user-set fixed indoor temperature), stored as the <c>mode</c> column in the PK of
/// every learned aggregate and baseline. A rise measured over a fixed 21.5° and a rise
/// measured over the true outside temperature are physically incomparable, so they must never
/// blend, and the column is what keeps them apart.</para>
///
/// <para><b>Cooling setup is the same kind of axis.</b> Reported by a user with an adjustable
/// sealed laptop cooler (Ilano V12): turning its fans down raises every temperature, and
/// DeltaT read that as the CPU's and GPU's liquid metal and paste degrading. It is the
/// documented "no case airflow awareness" limitation, but hitting someone who changes airflow
/// daily. Recalibrate is the wrong tool at that cadence: it retires the baseline every time
/// they touch a knob. What they need is what fixed-indoor mode already has, a second reference
/// that coexists and is switched between.</para>
///
/// <para>So a regime is the PAIR, encoded into the existing column rather than added beside
/// it. <c>Id = AmbientMode + 2 x Profile</c>, which means profile 0 encodes to exactly the 0
/// and 1 that shipped: every existing row keeps its meaning, no table is rebuilt, and no query
/// changes. That last part is the safety argument, not just convenience. Every query in the
/// repository already filters on this column, so a cooling profile cannot leak across a filter
/// somebody forgot to update, which is precisely the bug this feature exists to prevent.</para></summary>
public readonly record struct Regime(int AmbientMode, int Profile)
{
    public int Id => AmbientMode + 2 * Profile;

    public static Regime FromId(int id) => new(id & 1, id >> 1);

    /// <summary>Settings-key suffix for this regime's per-regime markers (lock, earned,
    /// meter peak, score shown). The weather + default-profile regime keeps the original
    /// unsuffixed keys and the fixed + default-profile regime keeps ".fixed", so an existing
    /// install's markers keep working byte for byte.</summary>
    public string KeySuffix =>
        (AmbientMode == 1 ? ".fixed" : "") + (Profile == CoolingProfile.DefaultId ? "" : $".c{Profile}");
}

/// <summary>Reads and writes the cooling-setup list. Goes only through <see cref="SettingsStore"/>,
/// so it is unit-testable against a scratch database and holds no state of its own.
///
/// <para>Storage is one settings string: <c>id:name</c> records separated by newlines, with the
/// name's newlines and colons stripped on the way in. A list format rather than a table because
/// there are a handful of entries and they are read on every scoring pass.</para>
///
/// <para>The default profile is implicit: an install that never touches this feature stores
/// nothing, reports exactly one profile, and encodes to the regime ids that shipped.</para></summary>
public sealed class CoolingProfileStore
{
    private readonly SettingsStore _settings;

    public CoolingProfileStore(SettingsStore settings) => _settings = settings;

    /// <summary>Every declared setup, default first. Never empty.</summary>
    public IReadOnlyList<CoolingProfile> All()
    {
        var list = new List<CoolingProfile> { new(CoolingProfile.DefaultId, DefaultName()) };
        string raw = _settings.Get(SettingsKeys.CoolingProfiles) ?? "";
        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int sep = line.IndexOf(':');
            if (sep <= 0
                || !int.TryParse(line[..sep], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                || id <= CoolingProfile.DefaultId)
                continue;
            string name = line[(sep + 1)..].Trim();
            if (name.Length > 0 && list.All(p => p.Id != id))
                list.Add(new CoolingProfile(id, name));
        }
        return list;
    }

    private string DefaultName() =>
        _settings.Get(SettingsKeys.CoolingProfileDefaultName) is { Length: > 0 } n ? n : CoolingProfile.DefaultName;

    /// <summary>The setup DeltaT is currently learning and scoring against. Falls back to the
    /// default when the stored id names a setup that has since been deleted, so a stale
    /// setting can never point scoring at a regime with no rows.</summary>
    public int ActiveId
    {
        get
        {
            int id = _settings.GetInt(SettingsKeys.ActiveCoolingProfile) ?? CoolingProfile.DefaultId;
            return All().Any(p => p.Id == id) ? id : CoolingProfile.DefaultId;
        }
    }

    public CoolingProfile Active => All().First(p => p.Id == ActiveId);

    /// <summary>Switch the active setup. Nothing is wiped: the other setup's baseline stays
    /// exactly where it was and is picked up again on the next switch back, the same way the
    /// fixed-indoor toggle already behaves.</summary>
    public void SetActive(int id)
    {
        if (All().Any(p => p.Id == id))
            _settings.SetInt(SettingsKeys.ActiveCoolingProfile, id);
    }

    /// <summary>Adds a setup and returns it. Ids are assigned above every id ever used (tracked
    /// separately from the list, so deleting the highest one cannot hand its id to the next
    /// setup created and give it the deleted setup's learned rows).</summary>
    public CoolingProfile Add(string name, DateTimeOffset nowUtc)
    {
        int next = Math.Max(
            _settings.GetInt(SettingsKeys.CoolingProfileNextId) ?? 1,
            All().Max(p => p.Id) + 1);
        var profile = new CoolingProfile(next, Sanitize(name, next));
        var lines = All().Where(p => p.Id != CoolingProfile.DefaultId).Append(profile)
            .Select(p => $"{p.Id}:{p.Name}");
        _settings.Set(SettingsKeys.CoolingProfiles, string.Join('\n', lines));
        _settings.SetInt(SettingsKeys.CoolingProfileNextId, next + 1);
        // There is no data tagged with this setup before now, so its learning window floors here.
        _settings.SetTimestamp(SinceKey(next), nowUtc);
        return profile;
    }

    public void Rename(int id, string name)
    {
        if (id == CoolingProfile.DefaultId)
        {
            _settings.Set(SettingsKeys.CoolingProfileDefaultName, Sanitize(name, id));
            return;
        }
        var lines = All().Where(p => p.Id != CoolingProfile.DefaultId)
            .Select(p => p.Id == id ? p with { Name = Sanitize(name, id) } : p)
            .Select(p => $"{p.Id}:{p.Name}");
        _settings.Set(SettingsKeys.CoolingProfiles, string.Join('\n', lines));
    }

    /// <summary>Forgets a setup. The caller drops its learned rows and markers; this only
    /// removes it from the list and moves the active selection off it if it was selected. The
    /// default setup can't be deleted, since every machine has some cooling arrangement.</summary>
    public void Remove(int id)
    {
        if (id == CoolingProfile.DefaultId)
            return;
        var lines = All().Where(p => p.Id != CoolingProfile.DefaultId && p.Id != id)
            .Select(p => $"{p.Id}:{p.Name}");
        _settings.Set(SettingsKeys.CoolingProfiles, string.Join('\n', lines));
        _settings.Set(SinceKey(id), "");
        if (ActiveId == id)
            SetActive(CoolingProfile.DefaultId);
    }

    /// <summary>When this setup was declared. Its learning and recent windows floor here,
    /// because no aggregate row carries its tag before it existed. Null for the default setup,
    /// whose rows go back to the beginning of the epoch.</summary>
    public DateTimeOffset? SinceUtc(int id) =>
        id == CoolingProfile.DefaultId ? null : _settings.GetTimestamp(SinceKey(id));

    private static string SinceKey(int id) => $"{SettingsKeys.CoolingProfileSince}.{id}";

    /// <summary>Names are stored in a line/colon-delimited list and shown in a tray menu, so
    /// the delimiters and control characters come out and the length is bounded. An empty
    /// name would render as a blank menu row, so it falls back to something selectable.</summary>
    public static string Sanitize(string name, int id)
    {
        string clean = new(name.Where(ch => ch is not ('\n' or '\r' or ':') && !char.IsControl(ch)).ToArray());
        clean = clean.Trim();
        if (clean.Length > CoolingProfile.MaxNameLength)
            clean = clean[..CoolingProfile.MaxNameLength].TrimEnd();
        return clean.Length > 0 ? clean
            : id == CoolingProfile.DefaultId ? CoolingProfile.DefaultName : $"Setup {id}";
    }
}
