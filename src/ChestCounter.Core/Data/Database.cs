using System.Globalization;
using ChestCounter.Core.Analysis;
using Microsoft.Data.Sqlite;

namespace ChestCounter.Core.Data;

/// <summary>
/// The single local database (SQLite, WAL). The app process is the only writer; the widget and the web
/// dashboard both read through this class, so they can never disagree.
/// </summary>
public sealed class Database
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly object _writeGate = new();

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        lock (_writeGate)
        {
            using var c = Open();
            Exec(c, """
                CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE IF NOT EXISTS replay_files (
                    path TEXT PRIMARY KEY,
                    size INTEGER NOT NULL,
                    mtime_utc TEXT NOT NULL,
                    status TEXT NOT NULL,
                    dedupe_key TEXT,
                    match_id INTEGER,
                    error TEXT,
                    analyzer_version INTEGER NOT NULL DEFAULT 0,
                    recorded_local TEXT,
                    missing INTEGER NOT NULL DEFAULT 0,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS matches (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    dedupe_key TEXT NOT NULL UNIQUE,
                    file_path TEXT NOT NULL,
                    start_local TEXT NOT NULL,
                    start_utc TEXT NOT NULL,
                    duration_s INTEGER NOT NULL,
                    playlist TEXT,
                    recorder_found INTEGER NOT NULL,
                    account_id TEXT,
                    branch TEXT,
                    map TEXT,
                    placement INTEGER,
                    total_players INTEGER,
                    eliminations INTEGER,
                    opened INTEGER NOT NULL,
                    destroyed INTEGER NOT NULL,
                    gumgum_destroyed INTEGER NOT NULL,
                    normal INTEGER NOT NULL, rare INTEGER NOT NULL, buried INTEGER NOT NULL, relic INTEGER NOT NULL,
                    sprite INTEGER NOT NULL, other_chest INTEGER NOT NULL,
                    ammo INTEGER NOT NULL, food INTEGER NOT NULL, cannon INTEGER NOT NULL, other_container INTEGER NOT NULL,
                    unattributed_destroyed INTEGER NOT NULL,
                    unattributed_during_gumgum INTEGER NOT NULL,
                    team_chests INTEGER NOT NULL,
                    treasures INTEGER NOT NULL,
                    limit_excluded INTEGER NOT NULL DEFAULT 0,
                    analyzed_utc TEXT NOT NULL,
                    analyzer_version INTEGER NOT NULL,
                    notes TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_matches_start ON matches(start_utc);
                CREATE TABLE IF NOT EXISTS chest_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    match_id INTEGER NOT NULL REFERENCES matches(id) ON DELETE CASCADE,
                    t REAL NOT NULL,
                    container_id TEXT NOT NULL,
                    class_name TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    action TEXT NOT NULL,
                    gumgum INTEGER NOT NULL,
                    witnessed INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_events_match ON chest_events(match_id);
                CREATE TABLE IF NOT EXISTS sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    start_utc TEXT NOT NULL,
                    end_utc TEXT
                );
                """);
            // v2: pickaxe-only flag (added in place on existing databases; old rows default to "no")
            if (!HasColumn(c, "matches", "only_pickaxe"))
            {
                Exec(c, "ALTER TABLE matches ADD COLUMN only_pickaxe INTEGER NOT NULL DEFAULT 0");
            }

            if (GetMeta(c, "schema_version") is null)
            {
                SetMeta(c, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
                SetMeta(c, "first_run_utc", Iso(DateTime.UtcNow));
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // meta
    // ------------------------------------------------------------------------------------------------

    public string? GetMeta(string key)
    {
        using var c = Open();
        return GetMeta(c, key);
    }

    public void SetMeta(string key, string value)
    {
        lock (_writeGate)
        {
            using var c = Open();
            SetMeta(c, key, value);
        }
    }

    private static string? GetMeta(SqliteConnection c, string key)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection c, string key, string value)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------------------------------------
    // replay files
    // ------------------------------------------------------------------------------------------------

    public Dictionary<string, ReplayFileRecord> GetReplayFiles()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path,size,mtime_utc,status,dedupe_key,match_id,error,analyzer_version,recorded_local,missing,updated_utc FROM replay_files";
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<string, ReplayFileRecord>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
        {
            var rec = new ReplayFileRecord
            {
                Path = r.GetString(0),
                Size = r.GetInt64(1),
                MtimeUtc = ParseIso(r.GetString(2)),
                Status = r.GetString(3),
                DedupeKey = r.IsDBNull(4) ? null : r.GetString(4),
                MatchId = r.IsDBNull(5) ? null : r.GetInt64(5),
                Error = r.IsDBNull(6) ? null : r.GetString(6),
                AnalyzerVersion = r.GetInt32(7),
                RecordedLocal = r.IsDBNull(8) ? null : ParseIso(r.GetString(8)),
                Missing = r.GetInt32(9) != 0,
                UpdatedUtc = ParseIso(r.GetString(10)),
            };
            result[rec.Path] = rec;
        }
        return result;
    }

    public void UpsertReplayFile(ReplayFileRecord f)
    {
        lock (_writeGate)
        {
            using var c = Open();
            UpsertReplayFile(c, null, f);
        }
    }

    private static void UpsertReplayFile(SqliteConnection c, SqliteTransaction? tx, ReplayFileRecord f)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO replay_files(path,size,mtime_utc,status,dedupe_key,match_id,error,analyzer_version,recorded_local,missing,updated_utc)
            VALUES($p,$s,$m,$st,$dk,$mid,$e,$av,$rl,$mi,$u)
            ON CONFLICT(path) DO UPDATE SET size=excluded.size, mtime_utc=excluded.mtime_utc, status=excluded.status,
              dedupe_key=excluded.dedupe_key, match_id=excluded.match_id, error=excluded.error,
              analyzer_version=excluded.analyzer_version, recorded_local=excluded.recorded_local,
              missing=excluded.missing, updated_utc=excluded.updated_utc
            """;
        cmd.Parameters.AddWithValue("$p", f.Path);
        cmd.Parameters.AddWithValue("$s", f.Size);
        cmd.Parameters.AddWithValue("$m", Iso(f.MtimeUtc));
        cmd.Parameters.AddWithValue("$st", f.Status);
        cmd.Parameters.AddWithValue("$dk", (object?)f.DedupeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", (object?)f.MatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)f.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$av", f.AnalyzerVersion);
        cmd.Parameters.AddWithValue("$rl", f.RecordedLocal is DateTime rl ? Iso(rl) : DBNull.Value);
        cmd.Parameters.AddWithValue("$mi", f.Missing ? 1 : 0);
        cmd.Parameters.AddWithValue("$u", Iso(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    public void MarkMissing(IEnumerable<string> paths)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            foreach (var p in paths)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE replay_files SET missing=1, updated_utc=$u WHERE path=$p";
                cmd.Parameters.AddWithValue("$p", p);
                cmd.Parameters.AddWithValue("$u", Iso(DateTime.UtcNow));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // matches
    // ------------------------------------------------------------------------------------------------

    public sealed record SaveResult(long MatchId, bool IsDuplicate, string? DuplicateOf, bool IsNew = false);

    /// <summary>
    /// Stores an analysed match (insert, or update when the same match is re-analysed) and its events, and
    /// records the replay file. A different file with the same match identity is flagged as a duplicate.
    /// </summary>
    public SaveResult SaveAnalysis(ReplayFileRecord file, MatchAnalysis m)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            long? existingId = null;
            string? existingPath = null;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT id, file_path FROM matches WHERE dedupe_key=$k";
                q.Parameters.AddWithValue("$k", m.DedupeKey);
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    existingId = r.GetInt64(0);
                    existingPath = r.GetString(1);
                }
            }

            if (existingId is long dupId && existingPath is not null
                && !string.Equals(existingPath, file.Path, StringComparison.OrdinalIgnoreCase)
                && File.Exists(existingPath))
            {
                // Same match already stored from another file that still exists: duplicate (e.g. a saved/renamed copy).
                file.Status = ReplayFileStatus.Duplicate;
                file.MatchId = dupId;
                file.DedupeKey = m.DedupeKey;
                UpsertReplayFile(c, tx, file);
                tx.Commit();
                return new SaveResult(dupId, true, existingPath);
            }

            var isNew = existingId is null;
            var counts = new Dictionary<ContainerKind, int>();
            foreach (var e in m.Events.Where(e => e.By == Attribution.Me))
            {
                counts[e.Kind] = counts.GetValueOrDefault(e.Kind) + 1;
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = existingId is null
                    ? """
                      INSERT INTO matches(dedupe_key,file_path,start_local,start_utc,duration_s,playlist,recorder_found,account_id,branch,map,
                        placement,total_players,eliminations,opened,destroyed,gumgum_destroyed,normal,rare,buried,relic,sprite,other_chest,
                        ammo,food,cannon,other_container,unattributed_destroyed,unattributed_during_gumgum,team_chests,treasures,limit_excluded,
                        analyzed_utc,analyzer_version,notes,only_pickaxe)
                      VALUES($dk,$fp,$sl,$su,$d,$pl,$rf,$acc,$br,$map,$pla,$tp,$el,$op,$de,$gg,$n,$ra,$bu,$re,$sp,$oc,$am,$fo,$ca,$oco,$ua,$uag,$tc,$tr,$le,$au,$av,$no,$px);
                      SELECT last_insert_rowid();
                      """
                    : """
                      UPDATE matches SET file_path=$fp,start_local=$sl,start_utc=$su,duration_s=$d,playlist=$pl,recorder_found=$rf,account_id=$acc,
                        branch=$br,map=$map,placement=$pla,total_players=$tp,eliminations=$el,opened=$op,destroyed=$de,gumgum_destroyed=$gg,
                        normal=$n,rare=$ra,buried=$bu,relic=$re,sprite=$sp,other_chest=$oc,ammo=$am,food=$fo,cannon=$ca,other_container=$oco,
                        unattributed_destroyed=$ua,unattributed_during_gumgum=$uag,team_chests=$tc,treasures=$tr,
                        analyzed_utc=$au,analyzer_version=$av,notes=$no,only_pickaxe=$px
                      WHERE id=$id;
                      SELECT $id;
                      """;
                cmd.Parameters.AddWithValue("$dk", m.DedupeKey);
                cmd.Parameters.AddWithValue("$fp", file.Path);
                cmd.Parameters.AddWithValue("$sl", Iso(m.MatchStartLocal ?? DateTime.MinValue));
                cmd.Parameters.AddWithValue("$su", Iso(m.MatchStartUtc ?? DateTime.MinValue));
                cmd.Parameters.AddWithValue("$d", m.DurationSeconds);
                cmd.Parameters.AddWithValue("$pl", (object?)m.Playlist ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rf", m.RecorderFound ? 1 : 0);
                cmd.Parameters.AddWithValue("$acc", (object?)m.AccountId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$br", (object?)m.Branch ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$map", (object?)m.Map ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pla", (object?)m.Placement ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tp", (object?)m.TotalPlayers ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$el", (object?)m.Eliminations ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$op", m.ChestsOpened);
                cmd.Parameters.AddWithValue("$de", m.ChestsDestroyed);
                cmd.Parameters.AddWithValue("$gg", m.GumGumChestsDestroyed);
                cmd.Parameters.AddWithValue("$n", counts.GetValueOrDefault(ContainerKind.NormalChest));
                cmd.Parameters.AddWithValue("$ra", counts.GetValueOrDefault(ContainerKind.RareChest));
                cmd.Parameters.AddWithValue("$bu", counts.GetValueOrDefault(ContainerKind.BuriedChest));
                cmd.Parameters.AddWithValue("$re", counts.GetValueOrDefault(ContainerKind.ExtractionRelicChest));
                cmd.Parameters.AddWithValue("$sp", counts.GetValueOrDefault(ContainerKind.SpritePosterChest));
                cmd.Parameters.AddWithValue("$oc", counts.GetValueOrDefault(ContainerKind.OtherChest));
                cmd.Parameters.AddWithValue("$am", counts.GetValueOrDefault(ContainerKind.AmmoBox));
                cmd.Parameters.AddWithValue("$fo", counts.GetValueOrDefault(ContainerKind.FoodBox));
                cmd.Parameters.AddWithValue("$ca", counts.GetValueOrDefault(ContainerKind.LootCannon));
                cmd.Parameters.AddWithValue("$oco", counts.GetValueOrDefault(ContainerKind.OtherContainer));
                cmd.Parameters.AddWithValue("$ua", m.UnattributedChestsDestroyed);
                cmd.Parameters.AddWithValue("$uag", m.UnattributedDestroyedDuringGumGum);
                cmd.Parameters.AddWithValue("$tc", m.Events.Count(e => e.By == Attribution.Teammate && e.Kind.IsChest()));
                cmd.Parameters.AddWithValue("$tr", m.Treasures);
                cmd.Parameters.AddWithValue("$le", 0); // only used by INSERT; ApplyInitialLimit sets it
                cmd.Parameters.AddWithValue("$au", Iso(DateTime.UtcNow));
                cmd.Parameters.AddWithValue("$av", m.AnalyzerVersion);
                cmd.Parameters.AddWithValue("$no", m.Notes.Count > 0 ? string.Join("\n", m.Notes) : DBNull.Value);
                cmd.Parameters.AddWithValue("$px", m.OnlyPickaxe ? 1 : 0);
                if (existingId is long id)
                {
                    cmd.Parameters.AddWithValue("$id", id);
                }
                existingId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var del = c.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM chest_events WHERE match_id=$id";
                del.Parameters.AddWithValue("$id", existingId);
                del.ExecuteNonQuery();
            }

            using (var ins = c.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO chest_events(match_id,t,container_id,class_name,kind,action,gumgum,witnessed) VALUES($m,$t,$c,$cl,$k,$a,$g,$w)";
                var pm = ins.Parameters.Add("$m", SqliteType.Integer);
                var pt = ins.Parameters.Add("$t", SqliteType.Real);
                var pc = ins.Parameters.Add("$c", SqliteType.Text);
                var pcl = ins.Parameters.Add("$cl", SqliteType.Text);
                var pk = ins.Parameters.Add("$k", SqliteType.Text);
                var pa = ins.Parameters.Add("$a", SqliteType.Text);
                var pg = ins.Parameters.Add("$g", SqliteType.Integer);
                var pw = ins.Parameters.Add("$w", SqliteType.Integer);
                foreach (var e in m.Events.Where(e => e.By == Attribution.Me))
                {
                    pm.Value = existingId;
                    pt.Value = e.Time;
                    pc.Value = e.ContainerId;
                    pcl.Value = e.ClassName;
                    pk.Value = e.Kind.ToString();
                    pa.Value = e.Action.ToString();
                    pg.Value = e.WithGumGum ? 1 : 0;
                    pw.Value = e.Witnessed ? 1 : 0;
                    ins.ExecuteNonQuery();
                }
            }

            file.Status = ReplayFileStatus.Analyzed;
            file.MatchId = existingId;
            file.DedupeKey = m.DedupeKey;
            file.Error = null;
            file.AnalyzerVersion = m.AnalyzerVersion;
            UpsertReplayFile(c, tx, file);
            tx.Commit();
            return new SaveResult(existingId!.Value, false, null, isNew);
        }
    }

    public List<MatchRecord> GetMatches()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,dedupe_key,file_path,start_local,start_utc,duration_s,playlist,recorder_found,account_id,branch,map,placement,total_players,
                   eliminations,opened,destroyed,gumgum_destroyed,normal,rare,buried,relic,sprite,other_chest,ammo,food,cannon,other_container,
                   unattributed_destroyed,unattributed_during_gumgum,team_chests,treasures,limit_excluded,analyzed_utc,analyzer_version,notes,
                   only_pickaxe
            FROM matches ORDER BY start_utc DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<MatchRecord>();
        while (r.Read())
        {
            int? NInt(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            string? NStr(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            list.Add(new MatchRecord
            {
                Id = r.GetInt64(0),
                DedupeKey = r.GetString(1),
                FilePath = r.GetString(2),
                StartLocal = ParseIso(r.GetString(3)),
                StartUtc = DateTime.SpecifyKind(ParseIso(r.GetString(4)), DateTimeKind.Utc),
                DurationSeconds = r.GetInt32(5),
                Playlist = NStr(6),
                RecorderFound = r.GetInt32(7) != 0,
                AccountId = NStr(8),
                Branch = NStr(9),
                Map = NStr(10),
                Placement = NInt(11),
                TotalPlayers = NInt(12),
                Eliminations = NInt(13),
                Opened = r.GetInt32(14),
                Destroyed = r.GetInt32(15),
                GumGumDestroyed = r.GetInt32(16),
                Normal = r.GetInt32(17),
                Rare = r.GetInt32(18),
                Buried = r.GetInt32(19),
                Relic = r.GetInt32(20),
                Sprite = r.GetInt32(21),
                OtherChest = r.GetInt32(22),
                Ammo = r.GetInt32(23),
                Food = r.GetInt32(24),
                Cannon = r.GetInt32(25),
                OtherContainer = r.GetInt32(26),
                UnattributedDestroyed = r.GetInt32(27),
                UnattributedDuringGumGum = r.GetInt32(28),
                TeamChests = r.GetInt32(29),
                Treasures = r.GetInt32(30),
                LimitExcluded = r.GetInt32(31) != 0,
                AnalyzedUtc = ParseIso(r.GetString(32)),
                AnalyzerVersion = r.GetInt32(33),
                Notes = NStr(34),
                OnlyPickaxe = r.GetInt32(35) != 0,
            });
        }
        return list;
    }

    /// <summary>Marks which matches are outside the first import's "newest N eligible matches" window.</summary>
    public void SetLimitExcluded(IEnumerable<long> excludedIds)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using (var reset = c.CreateCommand())
            {
                reset.Transaction = tx;
                reset.CommandText = "UPDATE matches SET limit_excluded=0";
                reset.ExecuteNonQuery();
            }
            foreach (var id in excludedIds)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE matches SET limit_excluded=1 WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<ChestEventRecord> GetEvents(long matchId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT match_id,t,container_id,class_name,kind,action,gumgum,witnessed FROM chest_events WHERE match_id=$m ORDER BY t";
        cmd.Parameters.AddWithValue("$m", matchId);
        using var r = cmd.ExecuteReader();
        var list = new List<ChestEventRecord>();
        while (r.Read())
        {
            list.Add(new ChestEventRecord(r.GetInt64(0), (float)r.GetDouble(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6) != 0, r.GetInt32(7) != 0));
        }
        return list;
    }

    // ------------------------------------------------------------------------------------------------
    // sessions
    // ------------------------------------------------------------------------------------------------

    public List<SessionRecord> GetSessions()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,start_utc,end_utc FROM sessions ORDER BY start_utc DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRecord>();
        while (r.Read())
        {
            list.Add(new SessionRecord(r.GetInt64(0), Utc(r.GetString(1)), r.IsDBNull(2) ? null : Utc(r.GetString(2))));
        }
        return list;
    }

    /// <summary>Starts a session unless one is already active; returns the active session.</summary>
    public SessionRecord StartSession(DateTime nowUtc)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT id,start_utc FROM sessions WHERE end_utc IS NULL ORDER BY start_utc DESC LIMIT 1";
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    return new SessionRecord(r.GetInt64(0), Utc(r.GetString(1)), null);
                }
            }
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO sessions(start_utc,end_utc) VALUES($s,NULL); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$s", Iso(nowUtc));
            var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            tx.Commit();
            return new SessionRecord(id, nowUtc, null);
        }
    }

    /// <summary>Stops the active session (if any); returns it.</summary>
    public SessionRecord? StopSession(DateTime nowUtc)
    {
        lock (_writeGate)
        {
            using var c = Open();
            SessionRecord? active = null;
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT id,start_utc FROM sessions WHERE end_utc IS NULL ORDER BY start_utc DESC LIMIT 1";
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    active = new SessionRecord(r.GetInt64(0), Utc(r.GetString(1)), nowUtc);
                }
            }
            if (active is null)
            {
                return null;
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET end_utc=$e WHERE end_utc IS NULL";
            cmd.Parameters.AddWithValue("$e", Iso(nowUtc));
            cmd.ExecuteNonQuery();
            return active;
        }
    }

    public void DeleteSession(long id)
    {
        lock (_writeGate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------------------------------------

    private static bool HasColumn(SqliteConnection c, string table, string column)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    internal static DateTime ParseIso(string s) =>
        DateTime.ParseExact(s, "yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static DateTime Utc(string s) => DateTime.SpecifyKind(ParseIso(s), DateTimeKind.Utc);
}
