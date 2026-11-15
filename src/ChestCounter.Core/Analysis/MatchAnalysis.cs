namespace ChestCounter.Core.Analysis;

public enum ChestAction
{
    Opened,
    /// <summary>Destroyed (broken) before anyone opened it.</summary>
    Destroyed,
}

public enum Attribution
{
    Me,
    Teammate,
    Other,
    /// <summary>The replay does not say who did it (no SearchingPawn).</summary>
    Unknown,
}

/// <summary>One terminal interaction with a container (once per container per refill cycle).</summary>
public sealed record ContainerEvent(
    float Time,
    string ContainerId,
    string ClassName,
    ContainerKind Kind,
    ChestAction Action,
    Attribution By,
    /// <summary>The recorder had the Gum Gum fruit equipped when this happened (only meaningful for By == Me).</summary>
    bool WithGumGum,
    /// <summary>False when the transition itself was not in the replay (container re-appeared already opened).</summary>
    bool Witnessed);

/// <summary>One treasure received. <paramref name="SourceClass"/> is the chest it came from, when one was found.</summary>
public sealed record TreasureFind(float Time, bool Mine, string? SourceClass);

public enum ReplayStatus
{
    /// <summary>Parsed successfully; see the eligibility.</summary>
    Analyzed,
    /// <summary>Match still running or the game closed mid-match (no decryption key yet).</summary>
    Incomplete,
    /// <summary>Could not be read (corrupt / unsupported).</summary>
    Unreadable,
}

/// <summary>Everything the app knows about one match after analysis.</summary>
public sealed class MatchAnalysis
{
    public required string FilePath { get; init; }
    public required string DedupeKey { get; init; }
    public long FileSize { get; init; }
    public DateTime FileLastWriteUtc { get; init; }

    public ReplayStatus Status { get; set; }
    public string? Error { get; set; }

    public DateTime? MatchStartLocal { get; set; }
    public DateTime? MatchStartUtc { get; set; }
    public int DurationSeconds { get; set; }
    public DateTime? MatchEndUtc => MatchStartUtc?.AddSeconds(DurationSeconds);

    public string? Branch { get; set; }
    public string? Map { get; set; }
    public string? Playlist { get; set; }
    public string Mode { get; set; } = "Unknown";
    public MatchEligibility Eligibility { get; set; } = MatchEligibility.Unclassified;
    public string EligibilityReason { get; set; } = "";

    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }
    public int? Eliminations { get; set; }
    public bool RecorderFound { get; set; }
    public string? PlayerName { get; set; }
    public string? AccountId { get; set; }

    /// <summary>Distinct items (WeaponData) the recorder had equipped during the match.</summary>
    public List<string> RecorderItems { get; } = new();

    /// <summary>
    /// The replay shows the recorder equipping items, and every one of them was a pickaxe (AFK, spectating,
    /// or eliminated before picking anything up). False when there is no item information at all.
    /// </summary>
    public bool OnlyPickaxe { get; set; }

    /// <summary>All terminal container interactions seen in the replay (anyone).</summary>
    public List<ContainerEvent> Events { get; } = new();

    // ---- Recorder totals (derived from Events) ------------------------------------------------
    public int ChestsOpened => Events.Count(e => e.By == Attribution.Me && e.Kind.IsChest() && e.Action == ChestAction.Opened);
    public int ChestsDestroyed => Events.Count(e => e.By == Attribution.Me && e.Kind.IsChest() && e.Action == ChestAction.Destroyed);
    public int ChestsCredited => ChestsOpened + ChestsDestroyed;
    public int GumGumChestsDestroyed => Events.Count(e => e.By == Attribution.Me && e.Kind.IsChest() && e.Action == ChestAction.Destroyed && e.WithGumGum);

    public int MyCount(ContainerKind kind) => Events.Count(e => e.By == Attribution.Me && e.Kind == kind);

    /// <summary>Chests destroyed with no recorded culprit (not credited; shown for transparency).</summary>
    public int UnattributedChestsDestroyed => Events.Count(e => e.By == Attribution.Unknown && e.Kind.IsChest() && e.Action == ChestAction.Destroyed);

    /// <summary>Unattributed chest destructions while the recorder had the Gum Gum fruit equipped (not credited).</summary>
    public int UnattributedDestroyedDuringGumGum { get; set; }

    /// <summary>
    /// The replay recorded the recorder's ability list, so treasures can be seen. False for the few replays that
    /// do not record it: their treasure counts are unknown, not zero.
    /// </summary>
    public bool TreasuresTracked { get; set; }

    /// <summary>Every treasure the recorder received (treasure hunt), with who found it.</summary>
    public List<TreasureFind> TreasureFinds { get; } = new();

    /// <summary>Treasures you found (from a chest you opened or broke).</summary>
    public int Treasures => TreasureFinds.Count(t => t.Mine);

    /// <summary>Treasures a teammate found; the treasure hunt shares them with the whole team.</summary>
    public int TeamTreasures => TreasureFinds.Count(t => !t.Mine);

    public List<string> Notes { get; } = new();

    public TimeSpan ParseDuration { get; set; }
    public int AnalyzerVersion { get; set; }
}
