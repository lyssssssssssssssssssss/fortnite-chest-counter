namespace ChestCounter.Core.Data;

public static class ReplayFileStatus
{
    public const string Analyzed = "analyzed";
    public const string Incomplete = "incomplete";
    public const string Unreadable = "unreadable";
    public const string Invalid = "invalid";
    public const string BeforeCutoff = "before_cutoff";
    public const string Duplicate = "duplicate";
    public const string OutsideInitialLimit = "outside_initial_limit";
}

/// <summary>One .replay file seen in the replay folder.</summary>
public sealed class ReplayFileRecord
{
    public required string Path { get; init; }
    public long Size { get; set; }
    public DateTime MtimeUtc { get; set; }
    public string Status { get; set; } = "";
    public string? DedupeKey { get; set; }
    public long? MatchId { get; set; }
    public string? Error { get; set; }
    public int AnalyzerVersion { get; set; }
    public DateTime? RecordedLocal { get; set; }
    public bool Missing { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One analysed match (persisted even after Fortnite deletes the replay file).</summary>
public sealed class MatchRecord
{
    public long Id { get; set; }
    public required string DedupeKey { get; init; }
    public string FilePath { get; set; } = "";
    public DateTime StartLocal { get; set; }
    public DateTime StartUtc { get; set; }
    public int DurationSeconds { get; set; }
    public DateTime EndUtc => StartUtc.AddSeconds(DurationSeconds);

    public string? Playlist { get; set; }
    public bool RecorderFound { get; set; }
    public string? AccountId { get; set; }
    public string? Branch { get; set; }
    public string? Map { get; set; }
    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }
    public int? Eliminations { get; set; }

    public int Opened { get; set; }
    public int Destroyed { get; set; }
    public int Credited => Opened + Destroyed;
    public int GumGumDestroyed { get; set; }

    public int Normal { get; set; }
    public int Rare { get; set; }
    public int Buried { get; set; }
    public int Relic { get; set; }
    public int Sprite { get; set; }
    public int OtherChest { get; set; }
    public int Ammo { get; set; }
    public int Food { get; set; }
    public int Cannon { get; set; }
    public int OtherContainer { get; set; }

    public int UnattributedDestroyed { get; set; }
    public int UnattributedDuringGumGum { get; set; }
    public int TeamChests { get; set; }
    public int Treasures { get; set; }

    /// <summary>Excluded because it was older than the newest N eligible matches at the first import.</summary>
    public bool LimitExcluded { get; set; }

    /// <summary>The recorder only ever equipped a pickaxe (AFK / spectating / eliminated before looting).</summary>
    public bool OnlyPickaxe { get; set; }
    public DateTime AnalyzedUtc { get; set; }
    public int AnalyzerVersion { get; set; }
    public string? Notes { get; set; }
}

public sealed record ChestEventRecord(long MatchId, float Time, string ContainerId, string ClassName, string Kind, string Action, bool GumGum, bool Witnessed);

public sealed record SessionRecord(long Id, DateTime StartUtc, DateTime? EndUtc);
