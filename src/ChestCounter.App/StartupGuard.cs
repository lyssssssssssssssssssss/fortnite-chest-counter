using System.Diagnostics;
using ChestCounter.Core;

namespace ChestCounter.App;

/// <summary>
/// Start-up diagnostics and watchdog. Kept separate from <see cref="App"/> on purpose: a thread that touches a
/// class whose type initialiser is still running is blocked by the runtime, so the watchdog must not depend on
/// App's static state.
///
/// Once, right after Windows booted, a copy started by "Start with Windows" stalled before it could do anything
/// (and counted nothing). The watchdog logs a slow start after 30 s and, if start-up still has not completed
/// after 2 minutes, relaunches the app once.
/// </summary>
internal static class StartupGuard
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static volatile bool _complete;

    public static string[] Args { get; } = Environment.GetCommandLineArgs().Skip(1).ToArray();

    public static bool HasArg(string name) => Args.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsToolMode => HasArg("--exit") || HasArg("--render-widget") || HasArg("--render-consent");

    public static TimeSpan Elapsed => Clock.Elapsed;

    /// <summary>Called first thing in the process (before WPF loads any window or resource).</summary>
    public static void Begin()
    {
        if (IsToolMode)
        {
            return;
        }

        try
        {
            Log.Initialize(new AppPaths().LogPath);
            Log.Info($"Process {Environment.ProcessId} started{(Args.Length > 0 ? $" ({string.Join(' ', Args)})" : "")}");
        }
        catch
        {
            // logging must never stop the app
        }

        new Thread(Watch) { IsBackground = true, Name = "StartupWatchdog" }.Start();
    }

    /// <summary>The app core is up; anything after this waits on the user, not on Windows.</summary>
    public static void MarkComplete() => _complete = true;

    /// <summary>
    /// Test hook used only to verify the watchdog: simulates a stuck start-up at the point where a real one was
    /// seen (while WPF initialises). Never set in normal use.
    /// </summary>
    public static void SimulateStallIfRequested()
    {
        if (!HasArg("--relaunched")
            && int.TryParse(Environment.GetEnvironmentVariable("CHESTCOUNTER_TEST_STALL_SECONDS"), out var seconds) && seconds > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }
    }

    private static void Watch()
    {
        Thread.Sleep(TimeSpan.FromSeconds(30));
        if (_complete) return;
        Log.Warn($"Start-up is slow ({Clock.Elapsed.TotalSeconds:F0} s so far). Windows may still be busy starting up.");

        Thread.Sleep(TimeSpan.FromSeconds(90));
        if (_complete) return;

        if (HasArg("--relaunched"))
        {
            Log.Error("Start-up is still stuck after relaunching; giving up for now.");
            return;
        }

        // Relaunch once: a fresh process usually starts fine once Windows has settled.
        try
        {
            Log.Warn("Start-up has been stuck for 2 minutes; relaunching Chest Counter.");
            var args = Args.Append("--relaunched").Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args)) { UseShellExecute = false });
            Process.GetCurrentProcess().Kill();
        }
        catch (Exception ex)
        {
            Log.Error("Relaunch after a stuck start-up failed", ex);
        }
    }
}
