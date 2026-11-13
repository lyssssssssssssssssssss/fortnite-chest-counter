using System.Text.RegularExpressions;

namespace ChestCounter.Core.Analysis;

public enum MatchEligibility
{
    /// <summary>Public Battle Royale (build or Zero Build): counts everywhere.</summary>
    Eligible,
    /// <summary>Positively identified as a mode that must not count (ranked, tournament, Reload, Creative...).</summary>
    Excluded,
    /// <summary>Could not be confidently identified: excluded until a rule identifies it.</summary>
    Unclassified,
}

public sealed record EligibilityResult(MatchEligibility Eligibility, string ModeLabel, string Reason);

/// <summary>A user-editable rule: playlist name regex -> eligible/excluded + label.</summary>
public sealed record PlaylistRule(string Pattern, bool Eligible, string Label);

/// <summary>
/// Decides whether a match counts, from the playlist recorded in the replay (GameState.CurrentPlaylistInfo).
/// Allow-list design: only playlists that positively match a public Battle Royale rule are eligible.
/// Exclusion rules are checked first so that e.g. "Playlist_Habanero_..._Squad" can never slip through.
/// </summary>
public sealed class EligibilityClassifier
{
    /// <summary>
    /// Default rules. Playlist names observed in real replays on this PC (Fortnite 43.00):
    /// Playlist_NoBuildBR_Squad, Playlist_DefaultSquad, Playlist_DefaultDuo (public BR),
    /// Playlist_ShowdownTournament_RE_SourSpawnSolo_NPM (tournament), Playlist_Habanero_PiperBoot_Duos (ranked),
    /// Playlist_VK_Play (Creative/UEFN island).
    /// </summary>
    public static IReadOnlyList<PlaylistRule> DefaultRules { get; } =
    [
        // ---- exclusions (checked first) ----
        new(@"Showdown|Tournament|Cup|Event_|Competitive|Arena", false, "Tournament / competitive"),
        new(@"Habanero|Ranked", false, "Ranked"),
        new(@"Blastberry|Punchberry|Sunflower|Reload|Respawn_|_Respawn", false, "Reload"),
        new(@"VK_|Creative|Playground|Juno|Figment|Sparks|DelMar|Pilgrim|Blitz|Bots|Tutorial|LateGame|Solidgold|Unvaulted", false, "Other mode (Creative / LEGO / OG / Festival / LTM)"),

        // ---- public Battle Royale (allow-list) ----
        new(@"^Playlist_Default(Solo|Duo|Duos|Trio|Trios|Squad|Squads)$", true, "Battle Royale"),
        new(@"^Playlist_Trios$", true, "Battle Royale"),
        new(@"^Playlist_NoBuildBR_(Solo|Duo|Duos|Trio|Trios|Squad|Squads)$", true, "Zero Build"),
    ];

    private readonly List<(Regex Regex, PlaylistRule Rule)> _rules;

    /// <summary>Rules are evaluated in order; the first match wins.</summary>
    public EligibilityClassifier(IEnumerable<PlaylistRule>? rules = null)
    {
        _rules = (rules ?? DefaultRules)
            .Select(r => (new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), r))
            .ToList();
    }

    /// <summary>User overrides (exact playlist names) take precedence over the built-in rules.</summary>
    public static EligibilityClassifier Create(IEnumerable<string>? extraEligible, IEnumerable<string>? extraExcluded)
    {
        var rules = new List<PlaylistRule>();
        foreach (var name in extraExcluded ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
                rules.Add(new PlaylistRule($"^{Regex.Escape(name.Trim())}$", false, "Excluded by your settings"));
        }
        foreach (var name in extraEligible ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name))
                rules.Add(new PlaylistRule($"^{Regex.Escape(name.Trim())}$", true, "Battle Royale"));
        }
        rules.AddRange(DefaultRules);
        return new EligibilityClassifier(rules);
    }

    /// <summary>Strips a full asset path down to the playlist name ("/BRPlaylists/.../Playlist_X.Playlist_X" -> "Playlist_X").</summary>
    public static string PlaylistName(string path)
    {
        var s = path;
        var dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[(dot + 1)..];
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        return s;
    }

    public EligibilityResult Classify(IReadOnlyList<string> playlists)
    {
        if (playlists.Count == 0)
        {
            return new(MatchEligibility.Unclassified, "Unknown", "No playlist was recorded in the replay.");
        }

        // A match has one playlist; if several were seen, every one of them must be eligible.
        EligibilityResult? result = null;
        foreach (var path in playlists)
        {
            var name = PlaylistName(path);
            var r = ClassifyOne(name);
            if (r.Eligibility != MatchEligibility.Eligible)
            {
                return r;
            }
            result ??= r;
        }
        return result!;
    }

    private EligibilityResult ClassifyOne(string name)
    {
        foreach (var (regex, rule) in _rules)
        {
            if (!regex.IsMatch(name))
            {
                continue;
            }

            return rule.Eligible
                ? new(MatchEligibility.Eligible, $"{rule.Label} {TeamSize(name)}".Trim(), $"{name} is a public Battle Royale playlist.")
                : new(MatchEligibility.Excluded, rule.Label, $"{name} matches the \"{rule.Label}\" rule.");
        }

        return new(MatchEligibility.Unclassified, name, $"{name} is not a known public Battle Royale playlist.");
    }

    private static string TeamSize(string name) =>
        name.EndsWith("Solo", StringComparison.OrdinalIgnoreCase) ? "Solo"
        : name.Contains("Duo", StringComparison.OrdinalIgnoreCase) ? "Duos"
        : name.Contains("Trio", StringComparison.OrdinalIgnoreCase) ? "Trios"
        : name.Contains("Squad", StringComparison.OrdinalIgnoreCase) ? "Squads"
        : "";
}
