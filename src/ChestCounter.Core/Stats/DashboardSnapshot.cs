namespace ChestCounter.Core.Stats;

/// <summary>Everything the widget and the dashboard show, computed in one place from the database.</summary>
public sealed record DashboardSnapshot(
    long Version,
    DateTime GeneratedUtc,
    string DisplayName,
    string SeasonStart,
    OverallStats Overall,
    SessionView? ActiveSession,
    IReadOnlyList<SessionView> Sessions,
    MatchView? LastMatch,
    IReadOnlyList<DailyPoint> Daily,
    IReadOnlyList<TypeRow> ByType,
    IReadOnlyList<MatchView> Matches,
    IReadOnlyList<ExcludedView> Excluded,
    IReadOnlyList<CpgPoint> CpgHistory,
    LibraryView Library,
    AppInfo App,
    IReadOnlyList<Notice> Notices);

/// <summary>
/// "Never miss a match" messages. Kind: catchup | gap | autostart_off. Level: info | warn | hint.
/// </summary>
public sealed record Notice(string Kind, string Level, string Text, int Count, DateTime AtUtc);

public sealed record OverallStats(
    int TotalCredited,
    int Opened,
    int Destroyed,
    int GumGumDestroyed,
    int Matches,
    double Cpg,
    int BestMatch,
    long? BestMatchId,
    int SinceAppStart,
    /// <summary>Treasures you found in chests you opened or broke (treasure hunt).</summary>
    int Treasures,
    /// <summary>Treasures your teammates found; the treasure hunt shares them with the whole team.</summary>
    int TeamTreasures,
    /// <summary>Counted matches that could not be checked for treasures (replay deleted before treasures were tracked).</summary>
    int TreasureMatchesUnchecked,
    string TreasuresNote,
    int TeamChests,
    int UnattributedDestroyed,
    double? Last10Cpg,
    int MatchesWithGumGum,
    int PickaxeOnlySkipped,
    /// <summary>Every game you played since the season start (any mode, your account), counted or not.</summary>
    int GamesPlayed,
    int GamesPlayedToday);

public sealed record SessionView(
    long Id,
    DateTime StartUtc,
    DateTime? EndUtc,
    bool Active,
    int Chests,
    int Opened,
    int Destroyed,
    int Matches,
    double Average,
    int? LastMatchChests,
    int Best);

public sealed record MatchView(
    long Id,
    DateTime StartUtc,
    DateTime StartLocal,
    int DurationSeconds,
    string Mode,
    string? Playlist,
    int Opened,
    int Destroyed,
    int Credited,
    int GumGum,
    int Normal, int Rare, int Buried, int Relic, int Sprite, int OtherChest,
    int Ammo, int Food, int Cannon,
    int TeamChests,
    int UnattributedDestroyed,
    /// <summary>Treasures you found / your teammates found in this match; null when it was never checked for treasures.</summary>
    int? Treasures,
    int? TeamTreasures,
    int? Placement,
    int? TotalPlayers,
    int? Eliminations,
    string FileName,
    bool FileMissing);

public sealed record ExcludedView(
    string Kind,          // "match" | "replay"
    long? MatchId,
    DateTime? StartLocal,
    string FileName,
    string Category,      // Excluded / Unclassified / Incomplete / Unreadable / Duplicate / Before season / ...
    string Mode,
    string Reason,
    int Credited);

public sealed record DailyPoint(string Date, int Chests, int Matches);

public sealed record TypeRow(string Key, string Label, int Count, bool InChestTotal, bool IsSubRow);

public sealed record CpgPoint(int Index, DateTime StartLocal, int Credited, double Cumulative, double Rolling10);

public sealed record LibraryView(
    string? ReplayFolder,
    bool FolderFound,
    bool Busy,
    string Phase,
    int Done,
    int Total,
    bool MatchInProgress,
    DateTime? LastScanUtc,
    string? LastError,
    int ReplayFiles,
    int AnalyzedMatches,
    string? Account,
    bool AccessGranted);

public sealed record AppInfo(string Version, DateTime StartedUtc, string DashboardUrl, string DataFolder);
