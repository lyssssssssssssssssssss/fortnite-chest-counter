using ChestCounter.Core.Analysis;
using ChestCounter.Core.Data;
using ChestCounter.Core.Settings;

namespace ChestCounter.Core.Stats;

public sealed record MatchVerdict(bool Counted, MatchEligibility Eligibility, string Mode, string Reason, string? Category = null);

/// <summary>
/// The one place that decides whether a stored match counts. Used by every statistic (totals, CPG, best
/// match, sessions, graphs, history) and by the first-import limit, so they can never disagree.
/// A match counts only if ALL of these hold:
///   1. it was played on/after the season start date (date recorded inside the replay, local time);
///   2. the replay identifies the recorder (otherwise chests can't be attributed);
///   3. its playlist is a public Battle Royale / Zero Build playlist (allow-list; ranked, tournaments,
///      Reload, Creative and unknown playlists are excluded);
///   4. it belongs to the selected Epic account;
///   5. (setting, on by default) you held more than a pickaxe, or got at least one chest;
///   6. it is not older than the newest N eligible matches found by the first historical import.
/// </summary>
public sealed class MatchFilter
{
    public const string PickaxeOnlyCategory = "Pickaxe only";

    private readonly EligibilityClassifier _classifier;
    private readonly DateTime _seasonStartLocal;
    private readonly bool _applyLimitFlag;
    private readonly bool _excludePickaxeOnly;

    public MatchFilter(AppSettings settings, IReadOnlyList<MatchRecord> all, bool applyLimitFlag = true)
    {
        _classifier = EligibilityClassifier.Create(settings.ExtraEligiblePlaylists, settings.ExtraExcludedPlaylists);
        _seasonStartLocal = settings.SeasonStartLocal;
        _applyLimitFlag = applyLimitFlag;
        _excludePickaxeOnly = settings.ExcludePickaxeOnlyMatches;
        Account = ResolveAccount(settings.AccountFilter, all);
    }

    /// <summary>The Epic account being counted (null = not known / any).</summary>
    public string? Account { get; }

    public DateTime SeasonStartLocal => _seasonStartLocal;

    public static string? ResolveAccount(string filter, IReadOnlyList<MatchRecord> all)
    {
        if (!string.Equals(filter, "auto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(filter))
        {
            return filter.Trim();
        }

        return all.Where(m => !string.IsNullOrEmpty(m.AccountId))
            .GroupBy(m => m.AccountId!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(m => m.StartUtc))
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    public MatchVerdict Evaluate(MatchRecord m)
    {
        if (m.StartLocal < _seasonStartLocal)
        {
            return new(false, MatchEligibility.Excluded, "Before season", $"Played before the season start ({_seasonStartLocal:d MMM yyyy}).");
        }

        var e = _classifier.Classify(m.Playlist is null ? [] : [m.Playlist]);

        if (!m.RecorderFound)
        {
            return new(false, MatchEligibility.Unclassified, e.ModeLabel, "The replay does not identify which player recorded it.");
        }

        if (e.Eligibility != MatchEligibility.Eligible)
        {
            return new(false, e.Eligibility, e.ModeLabel, e.Reason);
        }

        if (Account is not null && m.AccountId is not null && !string.Equals(m.AccountId, Account, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, MatchEligibility.Excluded, e.ModeLabel, "Recorded by a different Epic account on this PC.");
        }

        if (_excludePickaxeOnly && m.OnlyPickaxe && m.Credited == 0)
        {
            return new(false, MatchEligibility.Excluded, e.ModeLabel,
                "You only held a pickaxe and got no chests (AFK, spectating, or eliminated before looting).", PickaxeOnlyCategory);
        }

        if (_applyLimitFlag && m.LimitExcluded)
        {
            return new(false, MatchEligibility.Eligible, e.ModeLabel, "Older than the most recent eligible matches imported on first launch.");
        }

        return new(true, MatchEligibility.Eligible, e.ModeLabel, e.Reason);
    }

    /// <summary>
    /// First-import rule: keep the newest <paramref name="limit"/> eligible matches (by the match date in the
    /// replay, not the file date); returns the ids of older eligible matches to exclude.
    /// </summary>
    public IReadOnlyList<long> SelectOutsideInitialLimit(IReadOnlyList<MatchRecord> all, int limit) =>
        all.Where(m => Evaluate(m).Counted)
           .OrderByDescending(m => m.StartUtc)
           .Skip(limit)
           .Select(m => m.Id)
           .ToList();
}
