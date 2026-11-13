using System.Diagnostics;
using ChestCounter.Core.Analysis;
using ChestCounter.Core.Data;
using ChestCounter.Core.Settings;
using ChestCounter.Core.Stats;
using ChestCounter.ReplayReader;

namespace ChestCounter.Core.Library;

public sealed record LibraryStatus(
    string? ReplayFolder,
    bool FolderFound,
    bool Busy,
    string Phase,
    int Done,
    int Total,
    int InProgressReplays,
    DateTime? LastScanUtc,
    string? LastError,
    bool InitialImportDone);

/// <summary>
/// Watches the replay folder and keeps the database in sync with it. Runs on one background thread at
/// below-normal priority. Replay files are only ever opened for reading, with shared access.
/// </summary>
public sealed class ReplayLibrary : IDisposable
{
    private readonly Database _db;
    private readonly SettingsStore _settings;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _statusGate = new();
    private readonly Thread _worker;
    private FileSystemWatcher? _watcher;
    private string? _watchedFolder;
    private volatile bool _stop;
    private volatile bool _reanalyzeAll;
    private long _lastFsEventTicks;
    private LibraryStatus _status = new(null, false, false, "Starting", 0, 0, 0, null, null, false);
    private readonly DateTime _processStartUtc;

    /// <summary>Fortnite keeps about this many unsaved replays and deletes the oldest ones.</summary>
    internal int FortniteReplayCap { get; set; } = 100;

    public ReplayLibrary(Database db, SettingsStore settings, DateTime? processStartUtc = null)
    {
        _db = db;
        _settings = settings;
        _processStartUtc = processStartUtc ?? DateTime.UtcNow;
        _settings.Changed += OnSettingsChanged;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ReplayLibrary", Priority = ThreadPriority.BelowNormal };
        var folder = ResolveReplayFolder(settings.Current.ReplayFolder);
        _status = new(folder, Directory.Exists(folder), true, "Starting", 0, 0, 0, null, null, db.GetMeta("initial_import_done") == "1");
    }

    /// <summary>Raised (on a background thread) whenever matches were added or changed.</summary>
    public event Action? MatchesChanged;

    /// <summary>Raised (on a background thread) whenever <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    public LibraryStatus Status
    {
        get { lock (_statusGate) return _status; }
    }

    public void Start() => _worker.Start();

    /// <summary>Shown while the user has not (yet) allowed reading the replay folder.</summary>
    public void SetWaitingForPermission() =>
        SetStatus(s => s with { Busy = false, Phase = "Waiting for permission to read your replay folder" });

    /// <summary>Runs one scan synchronously on the calling thread (tests only).</summary>
    internal void ScanNowForTests(bool reanalyze = false)
    {
        _reanalyzeAll = reanalyze;
        Scan();
    }

    public void RequestRescan() => _wake.Set();

    /// <summary>Re-analyses every replay still on disk (e.g. after changing the Gum Gum item setting).</summary>
    public void RequestReanalyzeAll()
    {
        _reanalyzeAll = true;
        _wake.Set();
    }

    /// <summary>Finds the replay folder: the configured one if it exists, else Fortnite's default.</summary>
    public static string ResolveReplayFolder(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured.Trim());
        }
        return AppPaths.DefaultReplayFolder;
    }

    private void OnSettingsChanged(AppSettings old, AppSettings updated)
    {
        if (!string.Equals(old.ReplayFolder ?? "", updated.ReplayFolder ?? "", StringComparison.OrdinalIgnoreCase)
            || old.SeasonStartDate != updated.SeasonStartDate)
        {
            _wake.Set();
        }

        if (!old.GumGumItemIds.SequenceEqual(updated.GumGumItemIds, StringComparer.OrdinalIgnoreCase))
        {
            RequestReanalyzeAll();
        }
    }

    private void SetStatus(Func<LibraryStatus, LibraryStatus> change)
    {
        lock (_statusGate)
        {
            _status = change(_status);
        }
        try { StatusChanged?.Invoke(); } catch (Exception ex) { Log.Error("StatusChanged handler failed", ex); }
    }

    private void RaiseMatchesChanged()
    {
        try { MatchesChanged?.Invoke(); } catch (Exception ex) { Log.Error("MatchesChanged handler failed", ex); }
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                Log.Error("Replay scan failed", ex);
                SetStatus(s => s with { Busy = false, Phase = "Error", LastError = ex.Message });
            }

            // Wait for a file event / request, or rescan every minute as a safety net.
            _wake.WaitOne(TimeSpan.FromSeconds(60));
            if (_stop) break;

            // Debounce bursts of file events (Fortnite writes the replay continuously during a match).
            while (!_stop)
            {
                var since = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFsEventTicks));
                if (since >= TimeSpan.FromSeconds(3)) break;
                Thread.Sleep(TimeSpan.FromSeconds(3) - since);
            }
        }
    }

    private void EnsureWatcher(string folder)
    {
        if (string.Equals(_watchedFolder, folder, StringComparison.OrdinalIgnoreCase) && _watcher is not null)
        {
            return;
        }

        _watcher?.Dispose();
        _watcher = null;
        _watchedFolder = folder;
        try
        {
            var w = new FileSystemWatcher(folder, "*.replay")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler onChange = (_, _) => OnFsEvent();
            w.Created += onChange;
            w.Changed += onChange;
            w.Deleted += onChange;
            w.Renamed += (_, _) => OnFsEvent();
            w.Error += (_, e) => { Log.Warn("File watcher error: " + e.GetException().Message); OnFsEvent(); };
            w.EnableRaisingEvents = true;
            _watcher = w;
            Log.Info($"Watching {folder}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not watch {folder}: {ex.Message} (falling back to periodic rescans)");
        }
    }

    private void OnFsEvent()
    {
        Interlocked.Exchange(ref _lastFsEventTicks, DateTime.UtcNow.Ticks);
        _wake.Set();
    }

    private void Scan()
    {
        var settings = _settings.Current;
        if (!settings.ReplayAccessGranted)
        {
            // Defence in depth: never touch the replay folder without the user's permission.
            SetWaitingForPermission();
            return;
        }

        var folder = ResolveReplayFolder(settings.ReplayFolder);
        var found = Directory.Exists(folder);
        var initialDone = _db.GetMeta("initial_import_done") == "1";
        SetStatus(s => s with { ReplayFolder = folder, FolderFound = found, InitialImportDone = initialDone });

        if (!found)
        {
            SetStatus(s => s with { Busy = false, Phase = "Replay folder not found", LastScanUtc = DateTime.UtcNow });
            return;
        }

        EnsureWatcher(folder);

        var cutoff = settings.SeasonStartLocal;
        var reanalyze = _reanalyzeAll;
        _reanalyzeAll = false;

        var known = _db.GetReplayFiles();
        var files = SafeEnumerate(folder);
        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        var missing = known.Values
            .Where(k => !k.Missing && !present.Contains(k.Path) && Path.GetDirectoryName(k.Path)?.Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
            .Select(k => k.Path).ToList();
        if (missing.Count > 0)
        {
            _db.MarkMissing(missing);
        }

        var candidates = new List<(ReplayFileRecord Rec, DateTime Recorded)>();
        var inProgress = 0;
        var presentRecorded = new List<DateTime>(); // match dates of the replays currently on disk

        foreach (var path in files)
        {
            if (_stop) return;
            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
                if (!fi.Exists) continue;
            }
            catch
            {
                continue;
            }

            known.TryGetValue(path, out var rec);
            var unchanged = rec is not null && !rec.Missing && rec.Size == fi.Length
                && Math.Abs((rec.MtimeUtc - fi.LastWriteTimeUtc).TotalSeconds) < 2;
            if (unchanged && rec!.RecordedLocal is DateTime knownDate)
            {
                presentRecorded.Add(knownDate);
            }

            if (unchanged)
            {
                var current = rec!.AnalyzerVersion >= MatchAnalyzer.Version;
                switch (rec.Status)
                {
                    case ReplayFileStatus.Analyzed or ReplayFileStatus.Duplicate or ReplayFileStatus.Unreadable or ReplayFileStatus.Invalid
                        when current && !reanalyze:
                        continue;
                    case ReplayFileStatus.OutsideInitialLimit:
                        continue;
                    case ReplayFileStatus.BeforeCutoff when rec.RecordedLocal is DateTime d && d < cutoff:
                        continue;
                }
            }

            var header = ReplayHeaderProbe.Probe(path);
            if (header.RecordedLocalTime is DateTime probedDate)
            {
                presentRecorded.Add(probedDate);
            }
            var newRec = new ReplayFileRecord
            {
                Path = path,
                Size = fi.Length,
                MtimeUtc = fi.LastWriteTimeUtc,
                RecordedLocal = header.RecordedLocalTime,
                AnalyzerVersion = MatchAnalyzer.Version,
                MatchId = rec?.MatchId,
                DedupeKey = rec?.DedupeKey,
            };

            if (header.State == ReplayFileState.InProgress)
            {
                newRec.Status = ReplayFileStatus.Incomplete;
                newRec.Error = header.Problem;
                _db.UpsertReplayFile(newRec);
                if (DateTime.UtcNow - fi.LastWriteTimeUtc < TimeSpan.FromMinutes(10))
                {
                    inProgress++; // being written right now = a match is in progress
                }
                continue;
            }

            if (header.State == ReplayFileState.Invalid || header.RecordedLocalTime is null)
            {
                newRec.Status = ReplayFileStatus.Invalid;
                newRec.Error = header.Problem ?? "The replay has no recorded match date.";
                _db.UpsertReplayFile(newRec);
                continue;
            }

            if (header.RecordedLocalTime.Value < cutoff)
            {
                // Outside the season: ignored entirely (not parsed, not counted).
                newRec.Status = ReplayFileStatus.BeforeCutoff;
                _db.UpsertReplayFile(newRec);
                continue;
            }

            candidates.Add((newRec, header.RecordedLocalTime.Value));
        }

        // Newest matches first, by the date recorded inside the replay (not the file date). For copies of the same
        // match, Fortnite's own "UnsavedReplay-..." file is processed first so it stays the canonical one.
        candidates.Sort((a, b) =>
        {
            var byDate = b.Recorded.CompareTo(a.Recorded);
            if (byDate != 0) return byDate;
            var aOwn = Path.GetFileName(a.Rec.Path).StartsWith("UnsavedReplay-", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            var bOwn = Path.GetFileName(b.Rec.Path).StartsWith("UnsavedReplay-", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            return aOwn != bOwn ? aOwn.CompareTo(bOwn) : string.Compare(a.Rec.Path, b.Rec.Path, StringComparison.OrdinalIgnoreCase);
        });

        SetStatus(s => s with { InProgressReplays = inProgress });

        if (candidates.Count > 0)
        {
            if (initialDone)
            {
                DetectPossibleGap(files.Count, presentRecorded);
            }

            var caughtUp = AnalyzeCandidates(candidates, settings, initialDone);
            if (caughtUp.Count > 0)
            {
                RecordCatchUp(caughtUp.Count, caughtUp.FirstUtc!.Value, caughtUp.LastUtc!.Value);
            }
        }

        if (!initialDone && !_stop)
        {
            ApplyInitialLimit(settings);
            _db.SetMeta("initial_import_done", "1");
            _db.SetMeta("initial_import_utc", Database.Iso(DateTime.UtcNow));
            RaiseMatchesChanged();
        }

        SetStatus(s => s with
        {
            Busy = false,
            Phase = inProgress > 0 ? "Waiting for the current match to finish" : "Watching for new replays",
            LastScanUtc = DateTime.UtcNow,
            LastError = null,
            InitialImportDone = true,
            Done = 0,
            Total = 0,
        });
    }

    /// <summary>
    /// Fortnite keeps only ~100 replays. If the folder is full and even its oldest replay was recorded after the
    /// last match we know about ended, replays in between may have been deleted before we could read them.
    /// </summary>
    private void DetectPossibleGap(int fileCount, List<DateTime> presentRecordedLocal)
    {
        if (fileCount < FortniteReplayCap - 5 || presentRecordedLocal.Count == 0)
        {
            return;
        }

        var matches = _db.GetMatches();
        if (matches.Count == 0)
        {
            return;
        }

        var lastKnownEnd = matches.Max(m => m.EndUtc);
        var oldestOnDisk = LocalToUtc(presentRecordedLocal.Min());
        if (oldestOnDisk > lastKnownEnd.AddMinutes(1))
        {
            var notice = new StoredNotice(0, lastKnownEnd, oldestOnDisk, DateTime.UtcNow);
            _db.SetMeta(StoredNotice.GapKey, notice.ToJson());
            Log.Warn($"Possible missed matches between {lastKnownEnd.ToLocalTime():yyyy-MM-dd HH:mm} and {oldestOnDisk.ToLocalTime():yyyy-MM-dd HH:mm}: " +
                     "Fortnite deleted replays before Chest Counter could read them.");
        }
    }

    private void RecordCatchUp(int count, DateTime firstUtc, DateTime lastUtc)
    {
        // Several scans right after start-up add up into one notice.
        var existing = StoredNotice.FromJson(_db.GetMeta(StoredNotice.CatchUpKey));
        var notice = existing is not null && existing.AtUtc >= _processStartUtc
            ? new StoredNotice(existing.Count + count, Min(existing.FromUtc, firstUtc), Max(existing.ToUtc, lastUtc), DateTime.UtcNow)
            : new StoredNotice(count, firstUtc, lastUtc, DateTime.UtcNow);
        _db.SetMeta(StoredNotice.CatchUpKey, notice.ToJson());
        Log.Info($"Caught up on {count} match(es) played while Chest Counter was not running.");

        static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
        static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    }

    private static DateTime LocalToUtc(DateTime local)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return DateTime.SpecifyKind(local - TimeZoneInfo.Local.BaseUtcOffset, DateTimeKind.Utc);
        }
    }

    private readonly record struct CatchUp(int Count, DateTime? FirstUtc, DateTime? LastUtc);

    private CatchUp AnalyzeCandidates(List<(ReplayFileRecord Rec, DateTime Recorded)> candidates, AppSettings settings, bool initialDone)
    {
        var caught = 0;
        DateTime? firstCaught = null, lastCaught = null;
        var analyzer = new MatchAnalyzer(new AnalyzerOptions
        {
            GumGumItems = new HashSet<string>(settings.GumGumItemIds, StringComparer.OrdinalIgnoreCase),
            Eligibility = EligibilityClassifier.Create(settings.ExtraEligiblePlaylists, settings.ExtraExcludedPlaylists),
        });
        var reader = new FactsReplayReader();
        var lastNotify = Stopwatch.StartNew();
        var changed = false;

        SetStatus(s => s with { Busy = true, Phase = "Analysing replays", Done = 0, Total = candidates.Count });

        for (var i = 0; i < candidates.Count && !_stop; i++)
        {
            var rec = candidates[i].Rec;
            try
            {
                var facts = reader.Read(rec.Path);
                var analysis = analyzer.Analyze(facts);

                switch (analysis.Status)
                {
                    case ReplayStatus.Incomplete:
                        rec.Status = ReplayFileStatus.Incomplete;
                        rec.Error = analysis.Error;
                        _db.UpsertReplayFile(rec);
                        break;
                    case ReplayStatus.Unreadable:
                        rec.Status = ReplayFileStatus.Unreadable;
                        rec.Error = analysis.Error;
                        _db.UpsertReplayFile(rec);
                        Log.Warn($"Unreadable replay {Path.GetFileName(rec.Path)}: {analysis.Error}");
                        break;
                    default:
                        var saved = _db.SaveAnalysis(rec, analysis);
                        changed = true;
                        // A brand-new match that had already finished before the app started = played while closed.
                        if (initialDone && saved.IsNew && analysis.MatchStartUtc is DateTime ms && analysis.MatchEndUtc < _processStartUtc)
                        {
                            caught++;
                            if (firstCaught is null || ms < firstCaught) firstCaught = ms;
                            if (lastCaught is null || ms > lastCaught) lastCaught = ms;
                        }
                        Log.Info(saved.IsDuplicate
                            ? $"Duplicate of {Path.GetFileName(saved.DuplicateOf)}: {Path.GetFileName(rec.Path)}"
                            : $"Analysed {Path.GetFileName(rec.Path)}: {analysis.Playlist} credited={analysis.ChestsCredited} ({analysis.ParseDuration.TotalSeconds:F1}s)");
                        break;
                }
            }
            catch (Exception ex)
            {
                // Never let one bad file stop the library.
                rec.Status = ReplayFileStatus.Unreadable;
                rec.Error = ex.Message;
                _db.UpsertReplayFile(rec);
                Log.Error($"Failed to analyse {rec.Path}", ex);
            }

            var done = i + 1;
            SetStatus(s => s with { Done = done });

            if (changed && lastNotify.ElapsedMilliseconds > 1000)
            {
                RaiseMatchesChanged();
                changed = false;
                lastNotify.Restart();
            }
        }

        if (changed)
        {
            RaiseMatchesChanged();
        }

        // Replay buffers are large (up to ~20 MB each); compact and give the memory back once a batch is done.
        reader = null!;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return new CatchUp(caught, firstCaught, lastCaught);
    }

    private void ApplyInitialLimit(AppSettings settings)
    {
        var all = _db.GetMatches();
        var filter = new MatchFilter(settings, all, applyLimitFlag: false);
        var outside = filter.SelectOutsideInitialLimit(all, settings.InitialMatchLimit);
        _db.SetLimitExcluded(outside);
        Log.Info($"Initial import complete: {all.Count} matches stored, {outside.Count} eligible matches beyond the newest {settings.InitialMatchLimit} excluded.");
    }

    private static List<string> SafeEnumerate(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.replay", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list {folder}: {ex.Message}");
            return [];
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _settings.Changed -= OnSettingsChanged;
        _watcher?.Dispose();
        if (_worker.IsAlive)
        {
            _worker.Join(TimeSpan.FromSeconds(5));
        }
    }
}
