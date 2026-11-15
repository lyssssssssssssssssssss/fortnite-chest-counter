using ChestCounter.Core.Analysis;
using ChestCounter.Core.Data;

namespace ChestCounter.Tests;

/// <summary>SYNTHETIC FIXTURES: persistence, duplicates, sessions and restarts against a temporary database.</summary>
public class DatabaseTests
{
    private static MatchAnalysis Analysis(string key, string path, int opened)
    {
        var m = new MatchAnalysis
        {
            FilePath = path,
            DedupeKey = key,
            Status = ReplayStatus.Analyzed,
            MatchStartLocal = new DateTime(2026, 11, 9, 20, 0, 0),
            MatchStartUtc = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
            DurationSeconds = 900,
            Playlist = "Playlist_NoBuildBR_Squad",
            RecorderFound = true,
            AnalyzerVersion = MatchAnalyzer.Version,
        };
        for (var i = 0; i < opened; i++)
        {
            m.Events.Add(new ContainerEvent(10 + i, $"c{i}", "Tiered_Chest_Athena_C", ContainerKind.NormalChest, ChestAction.Opened, Attribution.Me, false, true));
        }
        return m;
    }

    private static ReplayFileRecord File(string path) => new() { Path = path, Size = 1, MtimeUtc = DateTime.UtcNow };

    [Fact]
    public void Reanalysing_the_same_file_updates_instead_of_duplicating()
    {
        using var dir = new TempDir();
        var db = new Database(dir.File("t.db"));
        var path = dir.File("a.replay");
        db.SaveAnalysis(File(path), Analysis("guid:1", path, 3));
        db.SaveAnalysis(File(path), Analysis("guid:1", path, 5));
        var m = Assert.Single(db.GetMatches());
        Assert.Equal(5, m.Opened);
        Assert.Equal(5, db.GetEvents(m.Id).Count);
    }

    [Fact]
    public void A_copy_of_a_replay_is_detected_as_a_duplicate()
    {
        using var dir = new TempDir();
        var db = new Database(dir.File("t.db"));
        var original = dir.File("UnsavedReplay-1.replay");
        var copy = dir.File("My saved replay.replay");
        System.IO.File.WriteAllText(original, "x");
        System.IO.File.WriteAllText(copy, "x");

        db.SaveAnalysis(File(original), Analysis("guid:same", original, 4));
        var r = db.SaveAnalysis(File(copy), Analysis("guid:same", copy, 4));

        Assert.True(r.IsDuplicate);
        Assert.Single(db.GetMatches());
        Assert.Equal(ReplayFileStatus.Duplicate, db.GetReplayFiles()[copy].Status);
    }

    [Fact]
    public void Matches_survive_after_fortnite_deletes_the_replay()
    {
        using var dir = new TempDir();
        var db = new Database(dir.File("t.db"));
        var path = dir.File("gone.replay");
        db.SaveAnalysis(File(path), Analysis("guid:2", path, 7));
        db.MarkMissing([path]);
        Assert.Equal(7, Assert.Single(db.GetMatches()).Opened);
        Assert.True(db.GetReplayFiles()[path].Missing);
    }

    [Fact]
    public void Sessions_persist_across_app_restarts()
    {
        using var dir = new TempDir();
        var file = dir.File("t.db");
        var start = new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);

        var db1 = new Database(file);
        var s1 = db1.StartSession(start);
        var again = db1.StartSession(start.AddMinutes(5)); // starting twice keeps the same session
        Assert.Equal(s1.Id, again.Id);

        // "restart": a brand-new Database instance on the same file
        var db2 = new Database(file);
        var active = Assert.Single(db2.GetSessions());
        Assert.Null(active.EndUtc);
        Assert.Equal(start, active.StartUtc);

        var stopped = db2.StopSession(start.AddHours(2));
        Assert.NotNull(stopped);
        Assert.Equal(start.AddHours(2), new Database(file).GetSessions()[0].EndUtc);
        Assert.Null(db2.StopSession(start.AddHours(3)));
    }

    [Fact]
    public void Pickaxe_flag_is_stored_and_old_databases_are_upgraded_in_place()
    {
        using var dir = new TempDir();
        var file = dir.File("old.db");

        // Simulate a database created by the first release (no only_pickaxe column).
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE matches (id INTEGER PRIMARY KEY AUTOINCREMENT, dedupe_key TEXT NOT NULL UNIQUE, file_path TEXT NOT NULL, start_local TEXT NOT NULL, start_utc TEXT NOT NULL, duration_s INTEGER NOT NULL, playlist TEXT, recorder_found INTEGER NOT NULL, account_id TEXT, branch TEXT, map TEXT, placement INTEGER, total_players INTEGER, eliminations INTEGER, opened INTEGER NOT NULL, destroyed INTEGER NOT NULL, gumgum_destroyed INTEGER NOT NULL, normal INTEGER NOT NULL, rare INTEGER NOT NULL, buried INTEGER NOT NULL, relic INTEGER NOT NULL, sprite INTEGER NOT NULL, other_chest INTEGER NOT NULL, ammo INTEGER NOT NULL, food INTEGER NOT NULL, cannon INTEGER NOT NULL, other_container INTEGER NOT NULL, unattributed_destroyed INTEGER NOT NULL, unattributed_during_gumgum INTEGER NOT NULL, team_chests INTEGER NOT NULL, treasures INTEGER NOT NULL, limit_excluded INTEGER NOT NULL DEFAULT 0, analyzed_utc TEXT NOT NULL, analyzer_version INTEGER NOT NULL, notes TEXT);" +
                "INSERT INTO matches VALUES (1,'guid:old','x.replay','2026-11-05T10:00:00.000','2026-11-05T00:00:00.000',900,'Playlist_DefaultSquad',1,NULL,NULL,NULL,NULL,NULL,NULL,12,3,0,15,0,0,0,0,0,0,0,0,0,0,0,0,0,0,'2026-11-05T00:00:00.000',1,NULL);";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var db = new Database(file);
        var old = Assert.Single(db.GetMatches());
        Assert.Equal(12, old.Opened);
        Assert.False(old.OnlyPickaxe);
        Assert.Equal(0, old.TeamTreasures);
        Assert.False(old.TreasuresKnown); // analysed before treasures were tracked

        var path = dir.File("afk.replay");
        var afk = Analysis("guid:afk", path, 0);
        afk.OnlyPickaxe = true;
        db.SaveAnalysis(File(path), afk);
        Assert.True(db.GetMatches().Single(m => m.DedupeKey == "guid:afk").OnlyPickaxe);
    }

    [Fact]
    public void Treasures_round_trip()
    {
        using var dir = new TempDir();
        var db = new Database(dir.File("t.db"));
        var path = dir.File("t.replay");
        var m = Analysis("guid:t", path, 2);
        m.TreasureFinds.Add(new TreasureFind(10, true, "Tiered_Chest_Athena_C"));
        m.TreasureFinds.Add(new TreasureFind(50, false, null));
        m.TreasureFinds.Add(new TreasureFind(90, false, "Tiered_Chest_Athena_C"));
        m.TreasuresTracked = true;
        db.SaveAnalysis(File(path), m);

        var r = Assert.Single(db.GetMatches());
        Assert.Equal(1, r.Treasures);
        Assert.Equal(2, r.TeamTreasures);
        Assert.True(r.TreasuresKnown);
    }

    [Fact]
    public void Meta_and_limit_flags_round_trip()
    {
        using var dir = new TempDir();
        var db = new Database(dir.File("t.db"));
        var p1 = dir.File("1.replay");
        var p2 = dir.File("2.replay");
        var id1 = db.SaveAnalysis(File(p1), Analysis("guid:a", p1, 1)).MatchId;
        db.SaveAnalysis(File(p2), Analysis("guid:b", p2, 1));
        db.SetLimitExcluded([id1]);
        db.SetMeta("initial_import_done", "1");

        var reopened = new Database(dir.File("t.db"));
        Assert.Equal("1", reopened.GetMeta("initial_import_done"));
        Assert.True(reopened.GetMatches().Single(m => m.Id == id1).LimitExcluded);

        // re-analysis keeps the flag
        reopened.SaveAnalysis(File(p1), Analysis("guid:a", p1, 2));
        Assert.True(reopened.GetMatches().Single(m => m.Id == id1).LimitExcluded);
    }
}
