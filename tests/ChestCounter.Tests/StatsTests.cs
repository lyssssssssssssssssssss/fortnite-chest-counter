using ChestCounter.Core.Data;
using ChestCounter.Core.Library;
using ChestCounter.Core.Settings;
using ChestCounter.Core.Stats;

namespace ChestCounter.Tests;

/// <summary>SYNTHETIC FIXTURES: statistics, filters and sessions computed from hand-made match rows.</summary>
public class StatsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static long _ids;

    private static MatchRecord M(DateTime startLocal, int opened, int destroyed = 0, string playlist = "Playlist_NoBuildBR_Squad",
        string? account = "ME", int duration = 900, bool limitExcluded = false, int gum = 0, bool recorder = true,
        bool onlyPickaxe = false) => new()
    {
        OnlyPickaxe = onlyPickaxe,
        Id = Interlocked.Increment(ref _ids),
        DedupeKey = Guid.NewGuid().ToString(),
        FilePath = $"C:\\Demos\\{Guid.NewGuid():N}.replay",
        StartLocal = startLocal,
        StartUtc = DateTime.SpecifyKind(startLocal, DateTimeKind.Utc), // tests run with a UTC zone
        DurationSeconds = duration,
        Playlist = playlist,
        RecorderFound = recorder,
        AccountId = account,
        Opened = opened,
        Destroyed = destroyed,
        Normal = opened + destroyed,
        GumGumDestroyed = gum,
        LimitExcluded = limitExcluded,
    };

    private static DashboardSnapshot Compute(IReadOnlyList<MatchRecord> matches, AppSettings? settings = null,
        IReadOnlyList<SessionRecord>? sessions = null, DateTime? now = null, DateTime? appStart = null) =>
        StatsCalculator.Compute(new StatsInput(
            settings ?? new AppSettings { SeasonStartDate = "2026-11-01" },
            matches,
            sessions ?? [],
            new Dictionary<string, ReplayFileRecord>(),
            new LibraryStatus("C:\\Demos", true, false, "Idle", 0, 0, 0, null, null, true),
            now ?? new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
            appStart ?? new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc),
            1,
            new AppInfo("test", DateTime.UtcNow, "http://localhost:1/", "C:\\data"),
            Utc));

    [Fact]
    public void Cpg_matches_the_specification_example()
    {
        Assert.Equal(33.9, StatsCalculator.Cpg(1185, 35));
        Assert.Equal(0, StatsCalculator.Cpg(10, 0));
    }

    [Fact]
    public void Totals_are_opened_plus_destroyed_and_cpg_is_total_over_matches()
    {
        var s = Compute([
            M(new DateTime(2026, 11, 5, 10, 0, 0), 30, 5),
            M(new DateTime(2026, 11, 6, 10, 0, 0), 20, 10),
            M(new DateTime(2026, 11, 7, 10, 0, 0), 0, 0),
        ]);
        Assert.Equal(65, s.Overall.TotalCredited);
        Assert.Equal(50, s.Overall.Opened);
        Assert.Equal(15, s.Overall.Destroyed);
        Assert.Equal(3, s.Overall.Matches);
        Assert.Equal(21.7, s.Overall.Cpg);
        Assert.Equal(35, s.Overall.BestMatch);
        Assert.Equal(65, s.ByType.Where(t => t.InChestTotal && !t.IsSubRow).Sum(t => t.Count));
    }

    [Fact]
    public void Matches_before_the_season_start_are_ignored_everywhere()
    {
        var before = M(new DateTime(2026, 10, 31, 23, 59, 0), 99, 1);
        var onTheDay = M(new DateTime(2026, 11, 1, 0, 0, 0), 10);
        var s = Compute([before, onTheDay], now: new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(10, s.Overall.TotalCredited);
        Assert.Equal(1, s.Overall.Matches);
        Assert.Equal(10, s.Overall.BestMatch);
        Assert.Equal(10, s.Daily.Sum(d => d.Chests));
        Assert.Single(s.Matches);
        Assert.Single(s.CpgHistory);
        Assert.Contains(s.Excluded, x => x.MatchId == before.Id && x.Category == "Before season");
    }

    [Fact]
    public void Season_start_is_configurable()
    {
        var matches = new[] { M(new DateTime(2026, 11, 5, 10, 0, 0), 10), M(new DateTime(2026, 12, 2, 10, 0, 0), 20) };
        var next = Compute(matches, new AppSettings { SeasonStartDate = "2026-12-01" }, now: new DateTime(2026, 12, 3, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(20, next.Overall.TotalCredited);
        Assert.Equal(1, next.Overall.Matches);
    }

    [Fact]
    public void Ineligible_modes_never_contribute()
    {
        var s = Compute([
            M(new DateTime(2026, 11, 5, 10, 0, 0), 10),
            M(new DateTime(2026, 11, 5, 11, 0, 0), 50, playlist: "Playlist_Habanero_Squad"),
            M(new DateTime(2026, 11, 5, 12, 0, 0), 50, playlist: "Playlist_ShowdownTournament_RE_SourSpawnSolo_NPM"),
            M(new DateTime(2026, 11, 5, 13, 0, 0), 50, playlist: "Playlist_Blastberry_Squads"),
            M(new DateTime(2026, 11, 5, 14, 0, 0), 50, playlist: "Playlist_VK_Play"),
            M(new DateTime(2026, 11, 5, 15, 0, 0), 50, playlist: "Playlist_Unknown_New"),
            M(new DateTime(2026, 11, 5, 16, 0, 0), 50, recorder: false),
        ]);
        Assert.Equal(10, s.Overall.TotalCredited);
        Assert.Equal(1, s.Overall.Matches);
        Assert.Equal(10, s.Overall.BestMatch);
        Assert.Equal(10, s.Daily.Sum(d => d.Chests));
        Assert.Equal(6, s.Excluded.Count(x => x.Kind == "match"));
    }

    [Fact]
    public void Other_accounts_on_the_same_pc_are_excluded_automatically()
    {
        var s = Compute([
            M(new DateTime(2026, 11, 5, 10, 0, 0), 10, account: "ME"),
            M(new DateTime(2026, 11, 5, 11, 0, 0), 10, account: "ME"),
            M(new DateTime(2026, 11, 5, 12, 0, 0), 40, account: "SIBLING"),
        ]);
        Assert.Equal(20, s.Overall.TotalCredited);
        Assert.Equal("ME", s.Library.Account);
    }

    [Fact]
    public void Matches_outside_the_first_import_window_do_not_count()
    {
        var s = Compute([M(new DateTime(2026, 11, 5, 10, 0, 0), 10), M(new DateTime(2026, 11, 4, 10, 0, 0), 30, limitExcluded: true)]);
        Assert.Equal(10, s.Overall.TotalCredited);
    }

    [Fact]
    public void Initial_import_keeps_the_newest_eligible_matches_by_match_date()
    {
        var settings = new AppSettings { SeasonStartDate = "2026-11-01" };
        var start = new DateTime(2026, 11, 1, 8, 0, 0);
        // 120 eligible + 30 ranked, inserted in shuffled order (ids/file order must not matter)
        var eligible = Enumerable.Range(0, 120).Select(i => M(start.AddMinutes(20 * i), 5)).ToList();
        var ranked = Enumerable.Range(0, 30).Select(i => M(start.AddMinutes(20 * i + 5), 5, playlist: "Playlist_Habanero_Squad")).ToList();
        var all = eligible.Concat(ranked).OrderBy(_ => Guid.NewGuid()).ToList();

        var outside = new MatchFilter(settings, all, applyLimitFlag: false).SelectOutsideInitialLimit(all, 100);

        Assert.Equal(20, outside.Count);
        Assert.Equal(eligible.Take(20).Select(m => m.Id).OrderBy(x => x), outside.OrderBy(x => x));
    }

    [Fact]
    public void Fewer_than_the_limit_keeps_everything()
    {
        var settings = new AppSettings();
        var all = Enumerable.Range(0, 35).Select(i => M(new DateTime(2026, 11, 2).AddHours(i), 3)).ToList();
        Assert.Empty(new MatchFilter(settings, all, applyLimitFlag: false).SelectOutsideInitialLimit(all, 100));
    }

    [Fact]
    public void Session_counts_only_matches_completed_while_active()
    {
        var sStart = new DateTime(2026, 11, 9, 18, 0, 0, DateTimeKind.Utc);
        var sEnd = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        var s = Compute([
            M(new DateTime(2026, 11, 9, 17, 30, 0), 99, duration: 900),  // ended 17:45 - before
            M(new DateTime(2026, 11, 9, 17, 50, 0), 40, duration: 900),  // started before, ended 18:05 - counts
            M(new DateTime(2026, 11, 9, 18, 30, 0), 57, 3, duration: 900),
            M(new DateTime(2026, 11, 9, 19, 50, 0), 70, duration: 900),  // ends 20:05 - after stop
            M(new DateTime(2026, 11, 9, 19, 0, 0), 33, playlist: "Playlist_Habanero_Squad"), // ranked - never
        ], sessions: [new SessionRecord(1, sStart, sEnd)]);

        var session = Assert.Single(s.Sessions);
        Assert.Equal(2, session.Matches);
        Assert.Equal(100, session.Chests);
        Assert.Equal(50, session.Average);
        Assert.Equal(60, session.LastMatchChests);
        Assert.Null(s.ActiveSession);
    }

    [Fact]
    public void Active_session_runs_until_now()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var s = Compute([M(new DateTime(2026, 11, 10, 11, 0, 0), 12, duration: 600)],
            sessions: [new SessionRecord(7, now.AddHours(-2), null)], now: now);
        Assert.NotNull(s.ActiveSession);
        Assert.Equal(12, s.ActiveSession!.Chests);
        Assert.Equal(1, s.ActiveSession.Matches);
    }

    [Fact]
    public void Chests_since_the_tool_started_counts_matches_finished_after_launch()
    {
        var appStart = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        var s = Compute([
            M(new DateTime(2026, 11, 10, 8, 0, 0), 10, duration: 600),
            M(new DateTime(2026, 11, 10, 8, 55, 0), 20, duration: 600), // ends 9:05
        ], appStart: appStart);
        Assert.Equal(20, s.Overall.SinceAppStart);
    }

    [Fact]
    public void Daily_series_covers_thirty_days_ending_today()
    {
        var s = Compute([M(new DateTime(2026, 11, 10, 9, 0, 0), 12), M(new DateTime(2026, 11, 8, 9, 0, 0), 7)]);
        Assert.Equal(30, s.Daily.Count);
        Assert.Equal("2026-11-10", s.Daily[^1].Date);
        Assert.Equal("2026-10-12", s.Daily[0].Date);
        Assert.Equal(12, s.Daily[^1].Chests);
        Assert.Equal(7, s.Daily[^3].Chests);
    }

    [Fact]
    public void Cpg_history_has_cumulative_and_rolling_averages()
    {
        var s = Compute(Enumerable.Range(0, 12).Select(i => M(new DateTime(2026, 11, 3).AddHours(i), i < 2 ? 0 : 10)).ToList());
        Assert.Equal(12, s.CpgHistory.Count);
        Assert.Equal(StatsCalculator.Cpg(100, 12), s.CpgHistory[^1].Cumulative);
        Assert.Equal(10, s.CpgHistory[^1].Rolling10);
    }

    [Fact]
    public void Pickaxe_only_matches_without_chests_are_not_counted_by_default()
    {
        var matches = new[]
        {
            M(new DateTime(2026, 11, 5, 10, 0, 0), 30, 6),
            M(new DateTime(2026, 11, 5, 11, 0, 0), 0, onlyPickaxe: true),   // AFK / spectating
            M(new DateTime(2026, 11, 5, 12, 0, 0), 0, onlyPickaxe: true),
            M(new DateTime(2026, 11, 5, 13, 0, 0), 2, onlyPickaxe: true),   // got chests -> always counts
            M(new DateTime(2026, 11, 5, 14, 0, 0), 0),                      // held weapons, 0 chests -> counts
        };

        var s = Compute(matches);
        Assert.Equal(38, s.Overall.TotalCredited);
        Assert.Equal(3, s.Overall.Matches);
        Assert.Equal(12.7, s.Overall.Cpg);
        Assert.Equal(2, s.Overall.PickaxeOnlySkipped);
        Assert.Equal(2, s.Excluded.Count(x => x.Category == MatchFilter.PickaxeOnlyCategory));

        var keep = Compute(matches, new AppSettings { ExcludePickaxeOnlyMatches = false });
        Assert.Equal(5, keep.Overall.Matches);
        Assert.Equal(7.6, keep.Overall.Cpg);
        Assert.Equal(0, keep.Overall.PickaxeOnlySkipped);
    }

    [Fact]
    public void Pickaxe_only_matches_never_count_towards_sessions()
    {
        var start = new DateTime(2026, 11, 9, 18, 0, 0, DateTimeKind.Utc);
        var s = Compute([
            M(new DateTime(2026, 11, 9, 18, 10, 0), 20, duration: 600),
            M(new DateTime(2026, 11, 9, 18, 30, 0), 0, duration: 600, onlyPickaxe: true),
        ], sessions: [new SessionRecord(1, start, start.AddHours(2))]);
        var session = Assert.Single(s.Sessions);
        Assert.Equal(1, session.Matches);
        Assert.Equal(20, session.Average);
    }

    [Fact]
    public void Games_played_counts_every_mode_this_season_on_my_account()
    {
        var s = Compute([
            M(new DateTime(2026, 11, 10, 9, 0, 0), 10),
            M(new DateTime(2026, 11, 10, 10, 0, 0), 0, onlyPickaxe: true),                 // played, not counted
            M(new DateTime(2026, 11, 9, 10, 0, 0), 5, playlist: "Playlist_Habanero_Squad"), // played, not counted
            M(new DateTime(2026, 10, 30, 10, 0, 0), 9),                                       // before season
            M(new DateTime(2026, 11, 9, 11, 0, 0), 9, account: "SIBLING"),                    // someone else
            M(new DateTime(2026, 11, 8, 11, 0, 0), 9),
        ]);
        Assert.Equal(4, s.Overall.GamesPlayed);
        Assert.Equal(2, s.Overall.GamesPlayedToday);
        Assert.Equal(2, s.Overall.Matches);
    }

    [Fact]
    public void Treasures_are_reported_as_unavailable_not_zero()
    {
        var s = Compute([M(new DateTime(2026, 11, 5, 10, 0, 0), 10)]);
        Assert.Null(s.Overall.Treasures);
        Assert.False(string.IsNullOrWhiteSpace(s.Overall.TreasuresNote));
    }
}
