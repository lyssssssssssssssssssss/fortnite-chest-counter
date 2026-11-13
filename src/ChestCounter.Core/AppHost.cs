using System.Reflection;
using ChestCounter.Core.Data;
using ChestCounter.Core.Library;
using ChestCounter.Core.Settings;
using ChestCounter.Core.Stats;
using ChestCounter.Core.Web;

namespace ChestCounter.Core;

/// <summary>
/// Composition root shared by the desktop app (and tests): one settings store, one database, one replay
/// library, one statistics service and one local web server - all in a single process.
/// </summary>
public sealed class AppHost : IDisposable
{
    public AppHost(string? dataDirectory = null)
    {
        Paths = new AppPaths(dataDirectory);
        Log.Initialize(Paths.LogPath);
        Log.Info($"Chest Counter {Version} starting (data: {Paths.DataDirectory})");

        Settings = new SettingsStore(Paths.SettingsPath);
        Database = new Database(Paths.DatabasePath);
        Library = new ReplayLibrary(Database, Settings, ProcessStartUtc);
        Stats = new StatsService(Database, Settings, Library, () => new AppInfo(Version, Stats!.AppStartedUtc, Web?.Url ?? "", Paths.DataDirectory));
        Web = new WebServer(Stats, start => start ? StartSession() : StopSession(), Library.RequestRescan);

        Library.MatchesChanged += Stats.Invalidate;
        Library.StatusChanged += OnLibraryStatus;
        Settings.Changed += (_, _) => Stats.Invalidate();
    }

    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "1.0.0";

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public Database Database { get; }
    public ReplayLibrary Library { get; }
    public StatsService Stats { get; }
    public WebServer Web { get; }

    /// <summary>When this process started (matches that ended before it and are new were "caught up").</summary>
    public DateTime ProcessStartUtc { get; } = DateTime.UtcNow;

    /// <summary>How the previous run of the app ended (read when the app starts).</summary>
    public PreviousRunInfo? PreviousRun { get; private set; }

    private string? _lastPhase;
    private bool _lastBusy;
    private bool _started;
    private bool _stopped;
    private Timer? _heartbeat;

    private void OnLibraryStatus()
    {
        // Only push status changes that matter to the UI (phase / busy), not every progress tick.
        var s = Library.Status;
        if (s.Phase != _lastPhase || s.Busy != _lastBusy || (s.Busy && s.Done % 10 == 0))
        {
            _lastPhase = s.Phase;
            _lastBusy = s.Busy;
            Stats.Invalidate();
        }
    }

    public void Start()
    {
        TrackRunStart();
        try
        {
            Web.Start(Settings.Current.WebPort);
        }
        catch (Exception ex)
        {
            Log.Error("Dashboard server failed to start", ex);
        }

        if (Settings.Current.ReplayAccessGranted)
        {
            StartLibrary();
        }
        else
        {
            // Nothing in the replay folder is read until the user says yes.
            Library.SetWaitingForPermission();
            Log.Info("Waiting for permission to read the replay folder.");
        }
    }

    private bool _libraryStarted;

    private void StartLibrary()
    {
        if (_libraryStarted) return;
        _libraryStarted = true;
        Library.Start();
    }

    /// <summary>The user allowed reading their replay folder (first-run prompt).</summary>
    public void GrantReplayAccess(string? replayFolder, bool startWithWindows)
    {
        Settings.Update(s =>
        {
            s.ReplayAccessGranted = true;
            s.ReplayAccessGrantedUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            s.ReplayFolder = string.IsNullOrWhiteSpace(replayFolder) ? null : replayFolder;
            s.StartWithWindows = startWithWindows;
        });
        Log.Info($"Permission given to read the replay folder ({Library.Status.ReplayFolder ?? replayFolder}).");
        if (_started)
        {
            StartLibrary();
        }
    }

    /// <summary>The user withdrew permission: stop reading replays (the app closes afterwards).</summary>
    public void RevokeReplayAccess()
    {
        Settings.Update(s =>
        {
            s.ReplayAccessGranted = false;
            s.ReplayAccessGrantedUtc = null;
            s.StartWithWindows = false;
        });
        Library.Dispose();
        Log.Info("Permission to read the replay folder was withdrawn.");
    }

    /// <summary>
    /// Records that the app is running (with a heartbeat every 5 minutes) and reports how the previous run
    /// ended, so a gap in counting can always be explained.
    /// </summary>
    private void TrackRunStart()
    {
        _started = true;
        PreviousRun = PreviousRunInfo.Read(Database);
        if (PreviousRun is { HadRun: true, EndedCleanly: false })
        {
            Log.Warn($"The previous run did not close normally (last seen {PreviousRun.LastAliveUtc?.ToLocalTime():yyyy-MM-dd HH:mm}). " +
                     "It was probably ended from Task Manager, or the PC lost power.");
        }
        else if (PreviousRun is { HadRun: true })
        {
            Log.Info($"The previous run closed normally at {PreviousRun.LastStopUtc?.ToLocalTime():yyyy-MM-dd HH:mm} ({PreviousRun.LastStopReason}).");
        }

        var now = Data.Database.Iso(DateTime.UtcNow);
        Database.SetMeta("clean_exit", "0");
        Database.SetMeta("last_start_utc", now);
        Database.SetMeta("last_alive_utc", now);
        _heartbeat = new Timer(_ =>
        {
            try { Database.SetMeta("last_alive_utc", Data.Database.Iso(DateTime.UtcNow)); } catch { }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        Stats.Invalidate();
    }

    /// <summary>Clean shutdown: records why the app stopped. Safe to call more than once.</summary>
    public void Stop(string reason)
    {
        if (_stopped) return;
        _stopped = true;
        Log.Info($"Chest Counter stopping ({reason})");
        _heartbeat?.Dispose();
        Web.Dispose();
        Library.Dispose();
        if (_started)
        {
            try
            {
                Database.SetMeta("last_stop_utc", Data.Database.Iso(DateTime.UtcNow));
                Database.SetMeta("last_stop_reason", reason);
                Database.SetMeta("clean_exit", "1");
            }
            catch (Exception ex)
            {
                Log.Error("Could not record the clean exit", ex);
            }
        }
    }

    public object StartSession()
    {
        var s = Database.StartSession(DateTime.UtcNow);
        Log.Info($"Session {s.Id} started");
        Stats.Invalidate();
        return new { ok = true, active = true, sessionId = s.Id, startUtc = s.StartUtc };
    }

    public object StopSession()
    {
        var s = Database.StopSession(DateTime.UtcNow);
        if (s is not null)
        {
            Log.Info($"Session {s.Id} stopped");
        }
        Stats.Invalidate();
        return new { ok = true, active = false, sessionId = s?.Id };
    }

    public void Dispose() => Stop("closed");
}

/// <summary>How the previous run of the app ended.</summary>
public sealed record PreviousRunInfo(bool HadRun, bool EndedCleanly, DateTime? LastAliveUtc, DateTime? LastStopUtc, string? LastStopReason)
{
    public static PreviousRunInfo Read(Data.Database db)
    {
        static DateTime? Utc(string? s) =>
            s is null ? null : DateTime.SpecifyKind(Data.Database.ParseIso(s), DateTimeKind.Utc);

        var clean = db.GetMeta("clean_exit");
        return new PreviousRunInfo(
            HadRun: clean is not null,
            EndedCleanly: clean != "0",
            LastAliveUtc: Utc(db.GetMeta("last_alive_utc")),
            LastStopUtc: Utc(db.GetMeta("last_stop_utc")),
            LastStopReason: db.GetMeta("last_stop_reason"));
    }
}
