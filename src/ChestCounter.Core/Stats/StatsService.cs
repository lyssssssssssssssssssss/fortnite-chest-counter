using ChestCounter.Core.Data;
using ChestCounter.Core.Library;
using ChestCounter.Core.Settings;

namespace ChestCounter.Core.Stats;

/// <summary>Caches the current <see cref="DashboardSnapshot"/>; invalidated whenever data or settings change.</summary>
public sealed class StatsService
{
    private readonly Database _db;
    private readonly SettingsStore _settings;
    private readonly ReplayLibrary _library;
    private readonly Func<AppInfo> _appInfo;
    private readonly object _gate = new();
    private DashboardSnapshot? _cached;
    private long _version = 1;

    public StatsService(Database db, SettingsStore settings, ReplayLibrary library, Func<AppInfo> appInfo)
    {
        _db = db;
        _settings = settings;
        _library = library;
        _appInfo = appInfo;
        AppStartedUtc = DateTime.UtcNow;
    }

    public DateTime AppStartedUtc { get; }

    /// <summary>Increments on every data change; clients use it to know when to refresh.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Raised after an invalidation (data, sessions or settings changed).</summary>
    public event Action<long>? Invalidated;

    public void Invalidate()
    {
        long v;
        lock (_gate)
        {
            _cached = null;
            v = Interlocked.Increment(ref _version);
        }
        try { Invalidated?.Invoke(v); } catch (Exception ex) { Log.Error("Invalidated handler failed", ex); }
    }

    public DashboardSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            // Sessions are time-dependent (an active session's window grows), so refresh at least every 30 s.
            if (_cached is not null && (DateTime.UtcNow - _cached.GeneratedUtc) < TimeSpan.FromSeconds(30))
            {
                return _cached;
            }

            var now = DateTime.UtcNow;
            _cached = StatsCalculator.Compute(new StatsInput(
                _settings.Current,
                _db.GetMatches(),
                _db.GetSessions(),
                _db.GetReplayFiles(),
                _library.Status,
                now,
                AppStartedUtc,
                Version,
                _appInfo(),
                Notices: BuildNotices(now)));
            return _cached;
        }
    }

    private IReadOnlyList<Notice> BuildNotices(DateTime nowUtc)
    {
        var list = new List<Notice>();
        static string L(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        var gap = StoredNotice.FromJson(_db.GetMeta(StoredNotice.GapKey));
        if (gap is not null && nowUtc - gap.AtUtc < TimeSpan.FromHours(48))
        {
            list.Add(new Notice("gap", "warn",
                $"Some matches may be missing: Fortnite only keeps about 100 replays and deleted the ones played between {L(gap.FromUtc)} and {L(gap.ToUtc)} " +
                "before Chest Counter could read them. Keeping Chest Counter running (it starts with Windows) prevents this.",
                0, gap.AtUtc));
        }

        var caught = StoredNotice.FromJson(_db.GetMeta(StoredNotice.CatchUpKey));
        if (caught is not null && nowUtc - caught.AtUtc < TimeSpan.FromHours(12))
        {
            var range = caught.Count == 1 ? L(caught.FromUtc) : $"{L(caught.FromUtc)} – {L(caught.ToUtc)}";
            list.Add(new Notice("catchup", "info",
                $"Caught up on {caught.Count} {(caught.Count == 1 ? "match" : "matches")} played while Chest Counter was closed ({range}). They are included in all your stats.",
                caught.Count, caught.AtUtc));
        }

        if (!_settings.Current.StartWithWindows)
        {
            list.Add(new Notice("autostart_off", "hint",
                "“Start with Windows” is off: matches played while Chest Counter is closed are only counted if their replays still exist when it starts (Fortnite keeps about 100). Turn it on in Settings.",
                0, nowUtc));
        }

        return list;
    }

    public MatchDetail? GetMatchDetail(long id)
    {
        var snap = GetSnapshot();
        var all = _db.GetMatches();
        var m = all.FirstOrDefault(x => x.Id == id);
        if (m is null)
        {
            return null;
        }
        var verdict = new MatchFilter(_settings.Current, all).Evaluate(m);
        var events = _db.GetEvents(id);
        return new MatchDetail(m, verdict, events, snap.Matches.FirstOrDefault(v => v.Id == id));
    }
}

public sealed record MatchDetail(MatchRecord Match, MatchVerdict Verdict, IReadOnlyList<ChestEventRecord> Events, MatchView? View);
