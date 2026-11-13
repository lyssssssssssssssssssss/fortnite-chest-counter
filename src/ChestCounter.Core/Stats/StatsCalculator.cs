using ChestCounter.Core.Data;
using ChestCounter.Core.Library;
using ChestCounter.Core.Settings;

namespace ChestCounter.Core.Stats;

public sealed record StatsInput(
    AppSettings Settings,
    IReadOnlyList<MatchRecord> Matches,
    IReadOnlyList<SessionRecord> Sessions,
    IReadOnlyDictionary<string, ReplayFileRecord> ReplayFiles,
    LibraryStatus Library,
    DateTime NowUtc,
    DateTime AppStartedUtc,
    long Version,
    AppInfo App,
    TimeZoneInfo? TimeZone = null,
    IReadOnlyList<Notice>? Notices = null);

/// <summary>
/// Pure computation of every displayed statistic from database rows. No I/O, so it is fully unit-testable.
///   Total credited chests = opened + destroyed (each chest counted once, see MatchAnalyzer).
///   CPG = total credited chests / number of counted matches.
/// </summary>
public static class StatsCalculator
{
    public static DashboardSnapshot Compute(StatsInput input)
    {
        var s = input.Settings;
        var filter = new MatchFilter(s, input.Matches);
        var tz = input.TimeZone ?? TimeZoneInfo.Local;

        var verdicts = input.Matches.ToDictionary(m => m.Id, filter.Evaluate);
        var counted = input.Matches.Where(m => verdicts[m.Id].Counted).OrderBy(m => m.StartUtc).ToList();

        // Every game played this season on your account, whatever the mode (the "just to see" count).
        var played = input.Matches.Where(m => m.StartLocal >= filter.SeasonStartLocal
            && (filter.Account is null || m.AccountId is null || string.Equals(m.AccountId, filter.Account, StringComparison.OrdinalIgnoreCase))).ToList();
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(input.NowUtc, tz).Date;

        // ---- overall --------------------------------------------------------------------------------
        var total = counted.Sum(m => m.Credited);
        var best = counted.OrderByDescending(m => m.Credited).ThenByDescending(m => m.StartUtc).FirstOrDefault();
        var last10 = counted.TakeLast(10).ToList();
        var overall = new OverallStats(
            TotalCredited: total,
            Opened: counted.Sum(m => m.Opened),
            Destroyed: counted.Sum(m => m.Destroyed),
            GumGumDestroyed: counted.Sum(m => m.GumGumDestroyed),
            Matches: counted.Count,
            Cpg: Cpg(total, counted.Count),
            BestMatch: best?.Credited ?? 0,
            BestMatchId: best?.Id,
            SinceAppStart: counted.Where(m => m.EndUtc >= input.AppStartedUtc).Sum(m => m.Credited),
            Treasures: null,
            TreasuresNote: "Treasures cannot yet be attributed to you reliably from replay data, so they are not counted.",
            TeamChests: counted.Sum(m => m.TeamChests),
            UnattributedDestroyed: counted.Sum(m => m.UnattributedDestroyed),
            Last10Cpg: last10.Count > 0 ? Cpg(last10.Sum(m => m.Credited), last10.Count) : null,
            MatchesWithGumGum: counted.Count(m => m.GumGumDestroyed > 0),
            PickaxeOnlySkipped: input.Matches.Count(m => verdicts[m.Id].Category == MatchFilter.PickaxeOnlyCategory),
            GamesPlayed: played.Count,
            GamesPlayedToday: played.Count(m => m.StartLocal.Date == todayLocal));

        // ---- views ----------------------------------------------------------------------------------
        MatchView View(MatchRecord m) => new(
            m.Id, m.StartUtc, m.StartLocal, m.DurationSeconds, verdicts[m.Id].Mode, m.Playlist,
            m.Opened, m.Destroyed, m.Credited, m.GumGumDestroyed,
            m.Normal, m.Rare, m.Buried, m.Relic, m.Sprite, m.OtherChest, m.Ammo, m.Food, m.Cannon,
            m.TeamChests, m.UnattributedDestroyed, m.Placement, m.TotalPlayers, m.Eliminations,
            Path.GetFileName(m.FilePath), !input.ReplayFiles.TryGetValue(m.FilePath, out var rf) || rf.Missing);

        var matchViews = counted.OrderByDescending(m => m.StartUtc).Select(View).ToList();
        var lastMatch = matchViews.FirstOrDefault();

        // ---- sessions -------------------------------------------------------------------------------
        SessionView SessionOf(SessionRecord r)
        {
            var end = r.EndUtc ?? input.NowUtc;
            // A match belongs to a session when it was *completed* while the session was active.
            var inSession = counted.Where(m => m.EndUtc >= r.StartUtc && m.EndUtc <= end).OrderBy(m => m.EndUtc).ToList();
            var chests = inSession.Sum(m => m.Credited);
            return new SessionView(r.Id, r.StartUtc, r.EndUtc, r.EndUtc is null, chests,
                inSession.Sum(m => m.Opened), inSession.Sum(m => m.Destroyed), inSession.Count,
                Cpg(chests, inSession.Count), inSession.LastOrDefault()?.Credited,
                inSession.Count > 0 ? inSession.Max(m => m.Credited) : 0);
        }

        var sessions = input.Sessions.OrderByDescending(r => r.StartUtc).Select(SessionOf).ToList();
        var active = sessions.FirstOrDefault(v => v.Active);

        // ---- chests per day (last 30 local days) ----------------------------------------------------
        var today = TimeZoneInfo.ConvertTimeFromUtc(input.NowUtc, tz).Date;
        var daily = new List<DailyPoint>(30);
        for (var d = today.AddDays(-29); d <= today; d = d.AddDays(1))
        {
            var day = d;
            var ms = counted.Where(m => m.StartLocal.Date == day).ToList();
            daily.Add(new DailyPoint(day.ToString("yyyy-MM-dd"), ms.Sum(m => m.Credited), ms.Count));
        }

        // ---- by type --------------------------------------------------------------------------------
        var byType = new List<TypeRow>
        {
            new("normal", "Chest", counted.Sum(m => m.Normal), true, false),
            new("buried", "Buried chest", counted.Sum(m => m.Buried), true, false),
            new("rare", "Rare chest", counted.Sum(m => m.Rare), true, false),
            new("relic", "Extraction / relic chest", counted.Sum(m => m.Relic), true, false),
            new("sprite", "Sprite poster chest", counted.Sum(m => m.Sprite), true, false),
        };
        var otherChests = counted.Sum(m => m.OtherChest);
        if (otherChests > 0)
        {
            byType.Add(new("other", "Other chest", otherChests, true, false));
        }
        byType.Add(new("destroyed", "of which destroyed chests (broken instead of opened)", overall.Destroyed, true, true));
        byType.Add(new("gumgum", "of which destroyed with the Gum Gum Devil Fruit", overall.GumGumDestroyed, true, true));
        byType.Add(new("ammo", "Ammo box (not in the chest total)", counted.Sum(m => m.Ammo), false, false));
        byType.Add(new("food", "Food box (not in the chest total)", counted.Sum(m => m.Food), false, false));
        byType.Add(new("cannon", "Loot cannon (not in the chest total)", counted.Sum(m => m.Cannon), false, false));

        // ---- CPG history ----------------------------------------------------------------------------
        var cpg = new List<CpgPoint>(counted.Count);
        var running = 0;
        for (var i = 0; i < counted.Count; i++)
        {
            running += counted[i].Credited;
            var window = counted.Skip(Math.Max(0, i - 9)).Take(Math.Min(10, i + 1)).ToList();
            cpg.Add(new CpgPoint(i + 1, counted[i].StartLocal, counted[i].Credited,
                Cpg(running, i + 1), Cpg(window.Sum(m => m.Credited), window.Count)));
        }

        // ---- excluded / problem replays (transparency) ----------------------------------------------
        var excluded = new List<ExcludedView>();
        foreach (var m in input.Matches.Where(m => !verdicts[m.Id].Counted).OrderByDescending(m => m.StartUtc))
        {
            var v = verdicts[m.Id];
            var category = v.Category is not null ? v.Category
                : m.StartLocal < filter.SeasonStartLocal ? "Before season"
                : m.LimitExcluded && v.Eligibility == Analysis.MatchEligibility.Eligible ? "Outside first import"
                : v.Eligibility.ToString();
            excluded.Add(new ExcludedView("match", m.Id, m.StartLocal, Path.GetFileName(m.FilePath), category, v.Mode, v.Reason, m.Credited));
        }
        foreach (var f in input.ReplayFiles.Values.Where(f => !f.Missing && f.Status is not ReplayFileStatus.Analyzed).OrderByDescending(f => f.RecordedLocal ?? f.MtimeUtc))
        {
            var (category, reason) = f.Status switch
            {
                ReplayFileStatus.Incomplete => ("Incomplete", f.Error ?? "Match still in progress or replay not finalised."),
                ReplayFileStatus.Unreadable => ("Unreadable", f.Error ?? "Could not be read."),
                ReplayFileStatus.Invalid => ("Invalid", f.Error ?? "Not a valid replay."),
                ReplayFileStatus.BeforeCutoff => ("Before season", $"Recorded before the season start ({filter.SeasonStartLocal:d MMM yyyy}); ignored."),
                ReplayFileStatus.Duplicate => ("Duplicate", "Same match as another replay file; counted once."),
                ReplayFileStatus.OutsideInitialLimit => ("Outside first import", "Older than the newest eligible matches at first launch."),
                _ => (f.Status, f.Error ?? ""),
            };
            excluded.Add(new ExcludedView("replay", f.MatchId, f.RecordedLocal, Path.GetFileName(f.Path), category, "", reason, 0));
        }

        // ---- library --------------------------------------------------------------------------------
        var lib = input.Library;
        var libView = new LibraryView(lib.ReplayFolder, lib.FolderFound, lib.Busy, lib.Phase, lib.Done, lib.Total,
            lib.InProgressReplays > 0, lib.LastScanUtc, lib.LastError,
            input.ReplayFiles.Values.Count(f => !f.Missing), input.Matches.Count, filter.Account, s.ReplayAccessGranted);

        return new DashboardSnapshot(input.Version, input.NowUtc, s.DisplayName, s.SeasonStartDate, overall, active, sessions,
            lastMatch, daily, byType, matchViews, excluded.Take(200).ToList(), cpg, libView, input.App, input.Notices ?? []);
    }

    /// <summary>Chests per game, rounded to one decimal (0 when there are no matches).</summary>
    public static double Cpg(int chests, int matches) =>
        matches <= 0 ? 0 : Math.Round((double)chests / matches, 1, MidpointRounding.AwayFromZero);
}
