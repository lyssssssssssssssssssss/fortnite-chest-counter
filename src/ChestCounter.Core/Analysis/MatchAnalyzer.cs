using ChestCounter.ReplayReader;

namespace ChestCounter.Core.Analysis;

public sealed class AnalyzerOptions
{
    /// <summary>Item definitions that identify the Gum Gum Devil Fruit (internal codename "TackyGoofiness").</summary>
    public HashSet<string> GumGumItems { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "WID_TackyGoofiness" };

    /// <summary>A destruction counts as "with the fruit" if the fruit was equipped within this many seconds before it.</summary>
    public float GumGumWindowSeconds { get; init; } = 3f;

    /// <summary>Two spawned (dynamic) containers of the same class closer than this (cm, horizontally) are the same container.</summary>
    public float DynamicSameSpotTolerance { get; init; } = 150f;

    public EligibilityClassifier Eligibility { get; init; } = new();

    /// <summary>The PC's time zone; replay timestamps are local wall-clock time.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
}

/// <summary>
/// Turns raw <see cref="ReplayFacts"/> into a <see cref="MatchAnalysis"/>.
///
/// Counting rules (each one derived from what real replays contain):
///  * Every searchable container replicates <c>bAlreadySearched</c>, <c>bDestroyed</c>,
///    <c>SearchAnimationCount</c> and <c>SearchingPawn</c> (the pawn the server credits: the opener, or whoever
///    broke it - including chests broken when a building is smashed with the Gum Gum fruit).
///  * The recorder is GameState.RecorderPlayerState; a pawn is the recorder's when Pawn.PlayerState matches.
///  * A container is credited once per "cycle": the first time it becomes opened or destroyed. A chest that
///    is opened is never counted again as destroyed. A refill (bAlreadySearched back to false) starts a new cycle.
///  * Channel re-sends are full snapshots (absent fields = defaults); they are compared as states, so a chest
///    re-appearing while already open is never counted twice.
///  * A destruction is only credited to a pawn when the game also recorded the loot spill
///    (SearchAnimationCount &gt; 0). Destructions with no recorded culprit are not credited to anyone.
///  * Spawned chests (buried, relic, extraction...) get a new network id each time they re-enter view, so they
///    are identified by class + position.
/// </summary>
public sealed class MatchAnalyzer
{
    /// <summary>Bump when counting rules change: replays still on disk are re-analysed automatically.</summary>
    public const int Version = 2;

    /// <summary>Harvesting tools (pickaxes), e.g. WID_Harvest_Pickaxe_FNCS.</summary>
    public static bool IsPickaxe(string item) =>
        item.Contains("Harvest", StringComparison.OrdinalIgnoreCase) || item.Contains("Pickaxe", StringComparison.OrdinalIgnoreCase);

    private readonly AnalyzerOptions _options;

    public MatchAnalyzer(AnalyzerOptions? options = null)
    {
        _options = options ?? new AnalyzerOptions();
    }

    public static string DedupeKeyOf(ReplayFacts f) =>
        f.ReplayGuid is { Length: > 0 } guid
            ? $"guid:{guid}"
            : $"meta:{f.RecordedLocalTime:yyyyMMddHHmmss}:{f.LengthMs}:{f.FileSize}";

    public MatchAnalysis Analyze(ReplayFacts f)
    {
        var m = new MatchAnalysis
        {
            FilePath = f.FilePath,
            DedupeKey = DedupeKeyOf(f),
            FileSize = f.FileSize,
            FileLastWriteUtc = f.FileLastWriteUtc,
            ParseDuration = f.ParseDuration,
            AnalyzerVersion = Version,
            Branch = f.Branch,
            Map = f.MapPath,
            Placement = f.Placement,
            TotalPlayers = f.TotalPlayers,
            Eliminations = f.Eliminations,
            DurationSeconds = (int)Math.Round(f.LengthMs / 1000.0),
        };

        if (f.RecordedLocalTime is DateTime local)
        {
            m.MatchStartLocal = local;
            m.MatchStartUtc = ToUtc(local);
        }

        if (f.Error is not null && !f.Completed)
        {
            var incomplete = f.IsLive || (f.IsEncrypted && !f.HasEncryptionKey)
                || f.Error.Contains("not yet marked as completed", StringComparison.OrdinalIgnoreCase)
                || f.Error.Contains("decryption key", StringComparison.OrdinalIgnoreCase);
            m.Status = incomplete ? ReplayStatus.Incomplete : ReplayStatus.Unreadable;
            m.Error = f.Error;
            m.Eligibility = MatchEligibility.Unclassified;
            m.EligibilityReason = incomplete
                ? "The match is still in progress, or Fortnite closed before the replay was finalised."
                : "The replay could not be read: " + f.Error;
            return m;
        }

        m.Status = ReplayStatus.Analyzed;
        m.Playlist = f.Playlists.Count > 0 ? EligibilityClassifier.PlaylistName(f.Playlists[0]) : null;
        var elig = _options.Eligibility.Classify(f.Playlists);
        m.Eligibility = elig.Eligibility;
        m.Mode = elig.ModeLabel;
        m.EligibilityReason = elig.Reason;

        var recorder = f.RecorderPlayerState ?? f.ControllerPlayerState;
        var myPawns = recorder is uint rec
            ? f.PawnToPlayerState.Where(kv => kv.Value == rec).Select(kv => kv.Key).ToHashSet()
            : new HashSet<uint>();
        m.RecorderFound = myPawns.Count > 0;
        if (recorder is uint rr)
        {
            m.PlayerName = f.PlayerStateName.GetValueOrDefault(rr);
            m.AccountId = f.PlayerStateAccount.GetValueOrDefault(rr);
        }

        // Items the recorder actually equipped (CurrentWeapon -> WeaponData). Unknown items count as "not a pickaxe",
        // so a match is only flagged pickaxe-only on positive evidence.
        var heldItems = f.PawnWeapons
            .Where(w => myPawns.Contains(w.Pawn) && w.WeaponActor != 0)
            .Select(w => f.WeaponActorItem.TryGetValue(w.WeaponActor, out var item) ? item : null)
            .ToList();
        m.RecorderItems.AddRange(heldItems.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
        m.OnlyPickaxe = heldItems.Count > 0 && heldItems.All(i => i is not null && IsPickaxe(i));

        if (!m.RecorderFound && m.Eligibility == MatchEligibility.Eligible)
        {
            // Without the recorder we cannot tell whose chests are whose: never guess.
            m.Eligibility = MatchEligibility.Unclassified;
            m.EligibilityReason = "The replay does not identify which player recorded it, so chests cannot be attributed.";
        }

        int? myTeam = recorder is uint r2 && f.PlayerStateTeam.TryGetValue(r2, out var t) ? t : null;

        Attribution Who(uint? pawn)
        {
            if (pawn is not uint p || p == 0) return Attribution.Unknown;
            if (myPawns.Contains(p)) return Attribution.Me;
            if (myTeam is int team && f.PawnToPlayerState.TryGetValue(p, out var ps)
                && f.PlayerStateTeam.TryGetValue(ps, out var pt) && pt == team)
            {
                return Attribution.Teammate;
            }
            return Attribution.Other;
        }

        var fruitIntervals = FruitIntervals(f, myPawns);
        bool FruitAround(float time) => fruitIntervals.Any(iv => iv.Start <= time && iv.End >= time - _options.GumGumWindowSeconds);

        RunStateMachine(f, m, Who, FruitAround);

        m.UnattributedDestroyedDuringGumGum = m.Events.Count(e =>
            e.By == Attribution.Unknown && e.Kind.IsChest() && e.Action == ChestAction.Destroyed && FruitAround(e.Time));

        var unwitnessed = m.Events.Count(e => e.By == Attribution.Me && !e.Witnessed);
        if (unwitnessed > 0)
        {
            m.Notes.Add($"{unwitnessed} of your container interactions first appeared in the replay already completed (Fortnite only sends idle containers once they change); they are credited because the replay names you as the searcher.");
        }

        return m;
    }

    private DateTime ToUtc(DateTime local)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _options.TimeZone);
        }
        catch (ArgumentException)
        {
            // invalid local time (DST gap): shift by the zone's base offset
            return DateTime.SpecifyKind(local - _options.TimeZone.BaseUtcOffset, DateTimeKind.Utc);
        }
    }

    private sealed class ContainerState
    {
        public bool Known, Searched, Destroyed, Counted;
        public uint? Pawn;
        public uint Anim;
    }

    private void RunStateMachine(ReplayFacts f, MatchAnalysis m, Func<uint?, Attribution> who, Func<float, bool> fruitAround)
    {
        var states = new Dictionary<string, ContainerState>(StringComparer.Ordinal);
        var dynamicKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var clusters = new List<(string Class, float X, float Y, string Key)>();

        string KeyOf(ContainerUpdate u)
        {
            if (!u.IsDynamic || u.X is null || u.Y is null)
            {
                return u.ContainerId;
            }
            if (dynamicKeys.TryGetValue(u.ContainerId, out var known))
            {
                return known;
            }

            string key;
            var i = clusters.FindIndex(c => c.Class == u.ClassName
                && Math.Abs(c.X - u.X.Value) < _options.DynamicSameSpotTolerance
                && Math.Abs(c.Y - u.Y.Value) < _options.DynamicSameSpotTolerance);
            if (i >= 0)
            {
                key = clusters[i].Key;
            }
            else
            {
                key = $"{u.ClassName}@{u.X.Value:F0},{u.Y.Value:F0}";
                clusters.Add((u.ClassName, u.X.Value, u.Y.Value, key));
            }
            dynamicKeys[u.ContainerId] = key;
            return key;
        }

        // Updates are recorded in stream order; a stable sort by time keeps same-frame order.
        foreach (var u in f.ContainerUpdates.OrderBy(u => u.Time))
        {
            var kind = ContainerKinds.Classify(u.ClassName);
            if (kind == ContainerKind.Ignored)
            {
                continue;
            }

            var key = KeyOf(u);
            if (!states.TryGetValue(key, out var st))
            {
                states[key] = st = new ContainerState();
            }

            var prevKnown = st.Known;
            var prevSearched = st.Searched;
            var prevDestroyed = st.Destroyed;

            if (u.FirstSeen)
            {
                // Initial replication of a channel: absent fields are at their defaults.
                st.Searched = u.AlreadySearched ?? false;
                st.Destroyed = u.Destroyed ?? false;
                st.Pawn = u.SearchingPawn is > 0 ? u.SearchingPawn : null;
                st.Anim = u.SearchAnimationCount ?? 0;
            }
            else
            {
                if (u.AlreadySearched is bool s) st.Searched = s;
                if (u.Destroyed is bool d) st.Destroyed = d;
                if (u.SearchingPawn is uint p) st.Pawn = p > 0 ? p : null;
                if (u.SearchAnimationCount is uint a) st.Anim = a;
            }
            st.Known = true;

            // Refill: an opened chest became searchable again -> new cycle.
            if (prevKnown && prevSearched && !st.Searched && !st.Destroyed)
            {
                st.Counted = false;
            }

            if (st.Counted)
            {
                continue;
            }

            var witnessed = prevKnown && !u.FirstSeen;
            if (st.Searched && !(prevKnown && prevSearched))
            {
                st.Counted = true;
                var by = who(st.Pawn);
                m.Events.Add(new ContainerEvent(u.Time, key, u.ClassName, kind, ChestAction.Opened, by, false, witnessed));
            }
            else if (st.Destroyed && !(prevKnown && prevDestroyed))
            {
                st.Counted = true;
                // Credit the breaker only when the loot spill was recorded (otherwise SearchingPawn may be
                // someone whose search was interrupted, not the destroyer).
                var by = st.Anim > 0 ? who(st.Pawn) : Attribution.Unknown;
                var gum = by == Attribution.Me && fruitAround(u.Time);
                m.Events.Add(new ContainerEvent(u.Time, key, u.ClassName, kind, ChestAction.Destroyed, by, gum, witnessed));
            }
        }
    }

    private List<(float Start, float End)> FruitIntervals(ReplayFacts f, HashSet<uint> myPawns)
    {
        var result = new List<(float, float)>();
        if (_options.GumGumItems.Count == 0)
        {
            return result;
        }

        foreach (var pawnGroup in f.PawnWeapons.Where(w => myPawns.Contains(w.Pawn)).GroupBy(w => w.Pawn))
        {
            var changes = pawnGroup.OrderBy(w => w.Time).ToList();
            for (var i = 0; i < changes.Count; i++)
            {
                var item = f.WeaponActorItem.TryGetValue(changes[i].WeaponActor, out var it) ? it : null;
                if (item is null || !_options.GumGumItems.Contains(item))
                {
                    continue;
                }
                // Last change of the pawn: cap the window (the pawn may have died holding the fruit).
                var end = i + 1 < changes.Count ? changes[i + 1].Time : changes[i].Time + 120f;
                result.Add((changes[i].Time, end));
            }
        }
        return result;
    }
}
