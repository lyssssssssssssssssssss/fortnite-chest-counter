using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ChestCounter.Core.Settings;
using ChestCounter.Core.Stats;

namespace ChestCounter.App;

/// <summary>
/// The compact always-available widget. It is a passive window: it never installs keyboard/mouse hooks,
/// never takes focus when it updates (WS_EX_NOACTIVATE) and only reacts to clicks made on it.
/// </summary>
public partial class WidgetWindow : Window
{
    private const double DesignWidth = 300;
    private const double DesignHeight = 264;

    private readonly App _app;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _refreshTimer;
    private bool _applyingSize;

    public WidgetWindow(App app)
    {
        _app = app;
        InitializeComponent();

        var s = app.Host.Settings.Current;
        Width = s.WidgetWidth;
        Height = s.WidgetHeight;
        if (s.WidgetLeft is double l && s.WidgetTop is double t)
        {
            Left = l;
            Top = t;
        }
        else
        {
            // Right edge, a little below the middle: clear of Fortnite's minimap (top right) and inventory (bottom right).
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height * 0.42;
        }
        ApplySettings(s);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePlacement(); };
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += OnSizeChanged;

        // Sessions are time based, so refresh periodically even without new data.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        app.Host.Stats.Invalidated += _ => Dispatcher.BeginInvoke(Refresh);
        app.Host.Settings.Changed += (_, updated) => Dispatcher.BeginInvoke(() => ApplySettings(updated));

        SourceInitialized += (_, _) =>
        {
            MakeNoActivate();
            EnsureOnScreen();
        };
        Loaded += (_, _) => Refresh();
    }

    // ------------------------------------------------------------------------------------------------
    // Window behaviour
    // ------------------------------------------------------------------------------------------------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Clicking/updating the widget never steals focus from the game, and it stays out of Alt+Tab.</summary>
    private void MakeNoActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void EnsureOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (Left + 40 > right || Left + Width - 40 < left || Top + 20 > bottom || Top < top - 10)
        {
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height * 0.42;
        }
    }

    private void ApplySettings(AppSettings s)
    {
        Topmost = s.WidgetTopmost;
        Opacity = s.WidgetOpacity;
        ResizeMode = s.WidgetLocked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        LockGlyph.Text = s.WidgetLocked ? "" : "";
        LockButton.ToolTip = s.WidgetLocked ? "Locked – click to allow moving and resizing" : "Unlocked – drag to move, drag the corner to resize";
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_applyingSize) return;
        // Keep the card's proportions so the content scales cleanly.
        _applyingSize = true;
        try
        {
            if (e.WidthChanged)
            {
                Height = Math.Round(Width * DesignHeight / DesignWidth);
            }
            else if (e.HeightChanged)
            {
                Width = Math.Round(Height * DesignWidth / DesignHeight);
            }
        }
        finally
        {
            _applyingSize = false;
        }
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SavePlacement()
    {
        if (!IsLoaded) return;
        var (l, t, w, h) = (Left, Top, Width, Height);
        _app.Host.Settings.Update(s =>
        {
            s.WidgetLeft = l;
            s.WidgetTop = t;
            s.WidgetWidth = w;
            s.WidgetHeight = h;
        });
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _app.OpenDashboard();
            return;
        }
        if (!_app.Host.Settings.Current.WidgetLocked && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the widget (Alt+F4) only minimises it to the tray; Exit is in the menu.
        e.Cancel = true;
        _app.HideWidget();
    }

    // ------------------------------------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------------------------------------

    private void Refresh()
    {
        DashboardSnapshot snap;
        try
        {
            snap = _app.Host.Stats.GetSnapshot();
        }
        catch (Exception ex)
        {
            Core.Log.Error("Widget refresh failed", ex);
            SetStatus("Database error", (Brush)FindResource("BadBrush"), ex.Message);
            return;
        }

        var c = CultureInfo.CurrentCulture;
        TotalText.Text = snap.Overall.TotalCredited.ToString("N0", c);
        LastBigText.Text = snap.LastMatch is { } lm ? lm.Credited.ToString("N0", c) : "–";

        if (snap.ActiveSession is { } s)
        {
            SessionMain.Text = $"{s.Chests.ToString("N0", c)} {(s.Chests == 1 ? "chest" : "chests")} ";
            SessionDetail.Text = s.Matches == 0
                ? "0 matches yet"
                : $"{s.Matches} {(s.Matches == 1 ? "match" : "matches")} · avg. {s.Average.ToString("0.#", c)}";
            SessionButton.Content = "■ Stop session";
            SessionButton.Style = (Style)FindResource("ChipButton");
            SessionButton.ToolTip = $"Session started {s.StartUtc.ToLocalTime():HH:mm}. Click to stop.";
        }
        else
        {
            SessionMain.Text = "not started";
            SessionDetail.Text = "";
            SessionButton.Content = "▶ Start session";
            SessionButton.Style = (Style)FindResource("GoldChipButton");
            SessionButton.ToolTip = "Start counting a playing session";
        }

        if (snap.LastMatch is { } last)
        {
            LastMain.Text = $"{last.Credited.ToString("N0", c)} {(last.Credited == 1 ? "chest" : "chests")} ";
            var place = last.Placement is int p ? $" · #{p}" : "";
            LastDetail.Text = $"{last.Opened} opened · {last.Destroyed} broken{place}";
        }
        else
        {
            LastMain.Text = "no match yet";
            LastDetail.Text = "";
        }

        var games = snap.Overall.GamesPlayed;
        GamesMain.Text = $"{games.ToString("N0", c)} played ";
        GamesDetail.Text = $"· {snap.Overall.GamesPlayedToday.ToString("N0", c)} today";
        GamesLine.ToolTip = $"Every game since {snap.SeasonStart} in any mode, including ranked, tournaments and pickaxe-only games.\n" +
                            $"{snap.Overall.Matches.ToString("N0", c)} of them count towards your chest stats.";

        var o = snap.Overall;
        TreasureText.Text = $"{o.Treasures.ToString("N0", c)} ";
        TreasureDetail.Text = o.TeamTreasures > 0 ? $"· +{o.TeamTreasures.ToString("N0", c)} from team" : "";
        TreasureLine.ToolTip = o.TreasuresNote;

        // Connection / activity indicator
        var lib = snap.Library;
        if (!lib.FolderFound)
        {
            SetStatus("No replay folder", (Brush)FindResource("BadBrush"), $"No replays at {lib.ReplayFolder}. Choose the folder in Settings, and make sure Fortnite's Record Replays option is on.");
        }
        else if (!_app.Host.Web.IsRunning)
        {
            SetStatus("Dashboard offline", (Brush)FindResource("BadBrush"), "The local dashboard server is not running.");
        }
        else if (lib.Busy)
        {
            SetStatus(lib.Total > 0 ? $"Analysing {lib.Done}/{lib.Total}" : "Analysing…", (Brush)FindResource("WarnBrush"), lib.Phase);
        }
        else if (lib.MatchInProgress)
        {
            SetStatus("In a match…", (Brush)FindResource("WarnBrush"), "Chests are counted a few seconds after the match ends (Fortnite finalises the replay then).");
        }
        else if (snap.Notices.FirstOrDefault(n => n.Kind == "gap") is { } gap)
        {
            SetStatus("Some matches missed", (Brush)FindResource("WarnBrush"), gap.Text);
        }
        else if (snap.Notices.FirstOrDefault(n => n.Kind == "catchup") is { } caught
                 && DateTime.UtcNow - caught.AtUtc < TimeSpan.FromMinutes(15))
        {
            SetStatus($"Caught up {caught.Count} {(caught.Count == 1 ? "match" : "matches")}", (Brush)FindResource("OkBrush"), caught.Text);
        }
        else
        {
            SetStatus($"Live · CPG {snap.Overall.Cpg.ToString("0.0", c)}", (Brush)FindResource("OkBrush"),
                $"Connected · {snap.Overall.Matches} eligible matches\nWatching {lib.ReplayFolder}\nDashboard: {snap.App.DashboardUrl}\nSeason start: {snap.SeasonStart}");
        }
    }

    private void SetStatus(string text, Brush dot, string tooltip)
    {
        StatusText.Text = text;
        StatusDot.Fill = dot;
        StatusDot.ToolTip = tooltip;
        StatusText.ToolTip = tooltip;
    }

    // ------------------------------------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------------------------------------

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        var s = _app.Host.Settings.Current;
        MenuTopmost.IsChecked = s.WidgetTopmost;
        MenuLock.IsChecked = s.WidgetLocked;
        MenuSession.Header = _app.Host.Stats.GetSnapshot().ActiveSession is not null ? "Stop session" : "Start session";
    }

    private void OnSessionClick(object sender, RoutedEventArgs e) => _app.ToggleSession();
    private void OnDashboardClick(object sender, RoutedEventArgs e) => _app.OpenDashboard();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => _app.OpenSettings();
    private void OnHideClick(object sender, RoutedEventArgs e) => _app.HideWidget();
    private void OnExitClick(object sender, RoutedEventArgs e) => _app.ExitApp("Exit from the widget menu");
    private void OnTopmostClick(object sender, RoutedEventArgs e) => _app.Host.Settings.Update(s => s.WidgetTopmost = !s.WidgetTopmost);
    private void OnLockClick(object sender, RoutedEventArgs e) => _app.Host.Settings.Update(s => s.WidgetLocked = !s.WidgetLocked);

    private void OnOpacityClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var o))
        {
            _app.Host.Settings.Update(s => s.WidgetOpacity = o);
        }
    }

    /// <summary>Renders the card (with current data) to a PNG – used for visual checks.</summary>
    public void RenderToPng(string path, double scale)
    {
        Width = DesignWidth;
        Height = DesignHeight;
        UpdateLayout();
        Refresh();
        UpdateLayout();
        var w = (int)Math.Ceiling(Card.ActualWidth * scale);
        var h = (int)Math.Ceiling(Card.ActualHeight * scale);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(Card);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void OnResetSizeClick(object sender, RoutedEventArgs e)
    {
        Width = DesignWidth;
        Height = DesignHeight;
        SavePlacement();
    }
}
