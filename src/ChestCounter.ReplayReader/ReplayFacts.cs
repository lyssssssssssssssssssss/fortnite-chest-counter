namespace ChestCounter.ReplayReader;

/// <summary>
/// Raw, uninterpreted facts extracted from one replay. Nothing in here is a "statistic": the counting
/// rules live in ChestCounter.Core so they can be unit-tested against synthetic facts.
/// </summary>
public sealed class ReplayFacts
{
    public required string FilePath { get; init; }
    public long FileSize { get; init; }
    public DateTime FileLastWriteUtc { get; init; }

    // ---- File info section --------------------------------------------------------------------
    /// <summary>Timestamp written by the game when recording started (local wall-clock time of the PC).</summary>
    public DateTime? RecordedLocalTime { get; set; }
    public uint LengthMs { get; set; }
    public bool IsLive { get; set; }
    public bool IsEncrypted { get; set; }
    public bool HasEncryptionKey { get; set; }
    public string? FriendlyName { get; set; }

    // ---- Header chunk -----------------------------------------------------------------------------
    public string? Branch { get; set; }
    public string? ReplayGuid { get; set; }
    public string? MapPath { get; set; }
    public List<string> GameSpecificData { get; } = new();
    public string? EngineNetworkVersion { get; set; }
    public string? NetworkVersion { get; set; }

    // ---- Game -----------------------------------------------------------------------------------
    /// <summary>Playlist asset paths seen in GameState.CurrentPlaylistInfo, in order of first appearance.</summary>
    public List<string> Playlists { get; } = new();
    public uint? RecorderPlayerState { get; set; }
    public uint? ControllerPlayerState { get; set; }
    /// <summary>Pawn actor NetGUID -> PlayerState NetGUID (from replicated Pawn.PlayerState).</summary>
    public Dictionary<uint, uint> PawnToPlayerState { get; } = new();
    /// <summary>PlayerState NetGUID -> team index (FortPlayerStateAthena.TeamIndex), when replicated.</summary>
    public Dictionary<uint, int> PlayerStateTeam { get; } = new();
    /// <summary>PlayerState NetGUID -> display name (PlayerNamePrivate), when replicated.</summary>
    public Dictionary<uint, string> PlayerStateName { get; } = new();
    /// <summary>PlayerState NetGUID -> Epic account id (UniqueID), when replicated.</summary>
    public Dictionary<uint, string> PlayerStateAccount { get; } = new();
    public int? Placement { get; set; }
    public int? TotalPlayers { get; set; }
    public int? Eliminations { get; set; }

    // ---- Containers -----------------------------------------------------------------------------
    public List<ContainerUpdate> ContainerUpdates { get; } = new();
    public List<ContainerClose> ContainerCloses { get; } = new();

    // ---- Items held by pawns (for attributing special destruction such as devil fruits) ---------
    /// <summary>Pawn NetGUID -> sequence of (time, weapon actor NetGUID).</summary>
    public List<PawnWeaponChange> PawnWeapons { get; } = new();
    /// <summary>Weapon actor NetGUID -> item definition path (WeaponData).</summary>
    public Dictionary<uint, string> WeaponActorItem { get; } = new();

    // ---- Treasure hunt ---------------------------------------------------------------------------
    /// <summary>
    /// Every GA_TreasureHandler_* ability that appeared in an ability list, in order. The game grants one for
    /// each treasure a player receives (the treasure hunt also shares a teammate's treasure with the team).
    /// </summary>
    public List<TreasureGrant> TreasureGrants { get; } = new();
    /// <summary>Ability list updates read / that could not be decoded (diagnostics).</summary>
    public int AbilityListUpdates { get; set; }
    /// <summary>
    /// Actor -> largest number of entries received in one update of its ability list. The full list (about 60
    /// abilities) normally arrives at the start of the match; some replays never record it, and then treasures
    /// cannot be seen in that replay.
    /// </summary>
    public Dictionary<uint, int> AbilityListLargestUpdate { get; } = new();
    public int AbilityListErrors { get; set; }

    // ---- Parse status ---------------------------------------------------------------------------
    public bool Completed { get; set; }
    public string? Error { get; set; }
    public TimeSpan ParseDuration { get; set; }

    // ---- Diagnostics (only filled when the reader runs with diagnostics on) ---------------------
    public ReplayDiagnostics? Diagnostics { get; set; }
}

/// <summary>One replicated update of a searchable container (chest, ammo box, ...).</summary>
public sealed record ContainerUpdate(
    float Time,
    string ContainerId,
    string ClassName,
    bool IsDynamic,
    float? X, float? Y, float? Z,
    uint? SearchingPawn,
    bool? AlreadySearched,
    bool? Destroyed,
    uint? SearchAnimationCount,
    bool FirstSeen);

/// <summary>The channel of a container actor was closed (destroyed, out of relevancy, dormant...).</summary>
public sealed record ContainerClose(float Time, string ContainerId, string ClassName, string Reason);

public sealed record PawnWeaponChange(float Time, uint Pawn, uint WeaponActor);

/// <summary>
/// A treasure handler ability given to the ability system component of <paramref name="Actor"/> (the actor
/// whose channel carried the list: the player's PlayerState).
/// </summary>
public sealed record TreasureGrant(float Time, uint Actor, int EntryId, string Ability);

public sealed class ReplayDiagnostics
{
    /// <summary>Group path -> property names known for it at the end of the replay (filtered).</summary>
    public SortedDictionary<string, List<string>> Groups { get; } = new(StringComparer.Ordinal);
    /// <summary>Class name -> number of actor channels opened.</summary>
    public Dictionary<string, int> ActorChannels { get; } = new();
    /// <summary>Unresolved replicated object path -> count.</summary>
    public Dictionary<string, int> Unresolved { get; } = new();
    /// <summary>"ClassNetCache.Field" -> count, for containers / game state / pawns.</summary>
    public Dictionary<string, int> ClassNetCacheFields { get; } = new();
    /// <summary>NetGUID paths matching interesting keywords.</summary>
    public SortedSet<string> KeywordPaths { get; } = new(StringComparer.Ordinal);
    /// <summary>Every NetGUID path seen (not serialized to JSON by the probe; written to a separate file).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SortedSet<string> AllPaths { get; } = new(StringComparer.Ordinal);
    /// <summary>"Group.Property" -> sample values (raw), for selected groups.</summary>
    public Dictionary<string, List<string>> PropertySamples { get; } = new();
    public List<string> EventGroups { get; } = new();
    /// <summary>"reason" / "reason:container" -> count of channel closes.</summary>
    public Dictionary<string, int> CloseReasons { get; } = new();
    /// <summary>Count of channel opens for container actors (to compare with closes).</summary>
    public int ContainerChannelOpens { get; set; }
    public int PacketErrors { get; set; }
}
