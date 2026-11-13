using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ChestCounter.Core;

namespace ChestCounter.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ChestCounter.SingleInstance";
    private const string ShowEventName = @"Local\ChestCounter.ShowWidget";
    private const string ExitEventName = @"Local\ChestCounter.Exit";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private EventWaitHandle? _exitSignal;
    private volatile bool _exiting;
    private TrayIcon? _tray;
    private WidgetWindow? _widget;
    private SettingsWindow? _settingsWindow;

    public AppHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Developer/QA aid: render the widget with current data to a PNG and exit (no server, no scanning).
        var renderIdx = Array.FindIndex(e.Args, a => a.Equals("--render-widget", StringComparison.OrdinalIgnoreCase));
        if (renderIdx >= 0 && renderIdx + 1 < e.Args.Length)
        {
            Host = new AppHost();
            var w = new WidgetWindow(this) { Left = -20000, Top = -20000, ShowActivated = false };
            w.Show();
            w.RenderToPng(e.Args[renderIdx + 1], scale: 2);
            Shutdown();
            return;
        }

        var consentIdx = Array.FindIndex(e.Args, a => a.Equals("--render-consent", StringComparison.OrdinalIgnoreCase));
        if (consentIdx >= 0 && consentIdx + 1 < e.Args.Length)
        {
            Host = new AppHost();
            var c = new ConsentWindow(Host) { Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            c.Show();
            c.RenderToPng(e.Args[consentIdx + 1], scale: 1.5);
            c.Close();
            Shutdown();
            return;
        }

        // "ChestCounter.exe --exit" asks a running instance to close cleanly (used by the installer/updater).
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // Already running: ask the running instance to show its widget, then quit.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        new Thread(() =>
        {
            if (_exitSignal.WaitOne() && !_exiting)
            {
                Dispatcher.BeginInvoke(() => ExitApp("closed by the installer/updater"));
            }
        }) { IsBackground = true, Name = "ExitSignal" }.Start();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (!_exiting && _showSignal.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(ShowWidget);
            }
        }) { IsBackground = true, Name = "ShowSignal" }.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

        Host = new AppHost();

        // First run on this PC: ask before reading anything in the replay folder.
        if (!Host.Settings.Current.ReplayAccessGranted)
        {
            var consent = new ConsentWindow(Host);
            if (consent.ShowDialog() != true)
            {
                Log.Info("The user did not allow reading the replay folder; nothing was read. Closing.");
                StartupRegistration.Apply(false);
                Host.Stop("permission to read the replay folder was not given");
                Shutdown();
                return;
            }
            Host.GrantReplayAccess(consent.ChosenFolder, consent.StartWithWindows);
        }

        Host.Start();
        StartupRegistration.Apply(Host.Settings.Current.StartWithWindows && Host.Settings.Current.ReplayAccessGranted);

        _widget = new WidgetWindow(this);
        _tray = new TrayIcon(this);

        // Windows shutting down / signing out: close cleanly so the stop is recorded (not seen as a crash).
        SessionEnding += (_, a) => ExitApp(a.ReasonSessionEnding == ReasonSessionEnding.Shutdown
            ? "Windows is shutting down"
            : "signing out of Windows");

        // Started with Windows: stay quiet (tray, widget only if you left it showing).
        // Opened by you (Start menu / shortcut): always show the widget.
        var fromAutostart = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        if (!fromAutostart)
        {
            ShowWidget();
        }
        else if (Host.Settings.Current.WidgetVisible)
        {
            _widget.Show();
        }
        if (Host.Settings.Current.OpenDashboardOnStart && !fromAutostart)
        {
            OpenDashboard();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true; // keep running: a display glitch must never stop the counter
    }

    public bool WidgetVisible => _widget?.IsVisible == true;

    public void ShowWidget()
    {
        if (_widget is null) return;
        _widget.Show();
        _widget.EnsureOnScreen();
        if (_widget.WindowState == WindowState.Minimized) _widget.WindowState = WindowState.Normal;
        Host.Settings.Update(s => s.WidgetVisible = true);
    }

    public void HideWidget()
    {
        _widget?.Hide();
        Host.Settings.Update(s => s.WidgetVisible = false);
    }

    public void ToggleWidget()
    {
        if (WidgetVisible) HideWidget(); else ShowWidget();
    }

    public void OpenDashboard()
    {
        var url = Host.Web.IsRunning ? Host.Web.Url : null;
        if (url is null)
        {
            MessageBox.Show("The dashboard server is not running. See the log in the data folder.", "Chest Counter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open browser", ex);
        }
    }

    public void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo(Host.Paths.DataDirectory) { UseShellExecute = true }); } catch { }
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ToggleSession()
    {
        var active = Host.Stats.GetSnapshot().ActiveSession is not null;
        if (active) Host.StopSession(); else Host.StartSession();
    }

    public void ExitApp(string reason)
    {
        if (_exiting) return;
        _exiting = true;
        try { _widget?.SavePlacement(); } catch { }
        _tray?.Dispose();
        try { Host.Stop(reason); } catch (Exception ex) { Log.Error("Shutdown error", ex); }
        _showSignal?.Set();
        _exitSignal?.Set();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
