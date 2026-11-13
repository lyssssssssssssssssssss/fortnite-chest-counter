using System.Globalization;
using System.Windows;
using Forms = System.Windows.Forms;

namespace ChestCounter.App;

/// <summary>Notification-area icon: the widget can be minimised here, and everything is reachable from its menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _sessionItem;
    private readonly Forms.ToolStripMenuItem _topmostItem;
    private readonly Forms.ToolStripMenuItem _lockItem;

    public TrayIcon(App app)
    {
        _app = app;
        var menu = new Forms.ContextMenuStrip();
        _showItem = new Forms.ToolStripMenuItem("Show widget", null, (_, _) => Ui(_app.ToggleWidget));
        var dashboard = new Forms.ToolStripMenuItem("Open dashboard", null, (_, _) => Ui(_app.OpenDashboard));
        _sessionItem = new Forms.ToolStripMenuItem("Start session", null, (_, _) => Ui(_app.ToggleSession));
        _topmostItem = new Forms.ToolStripMenuItem("Keep widget on top", null, (_, _) => Ui(() => _app.Host.Settings.Update(s => s.WidgetTopmost = !s.WidgetTopmost))) { CheckOnClick = false };
        _lockItem = new Forms.ToolStripMenuItem("Lock widget position", null, (_, _) => Ui(() => _app.Host.Settings.Update(s => s.WidgetLocked = !s.WidgetLocked)));
        var rescan = new Forms.ToolStripMenuItem("Rescan replays now", null, (_, _) => _app.Host.Library.RequestRescan());
        var settings = new Forms.ToolStripMenuItem("Settings…", null, (_, _) => Ui(_app.OpenSettings));
        var data = new Forms.ToolStripMenuItem("Open data folder", null, (_, _) => Ui(_app.OpenDataFolder));
        var exit = new Forms.ToolStripMenuItem("Exit", null, (_, _) => Ui(() => _app.ExitApp("Exit from the tray menu")));
        dashboard.Font = new System.Drawing.Font(dashboard.Font, System.Drawing.FontStyle.Bold);

        menu.Items.AddRange([_showItem, dashboard, new Forms.ToolStripSeparator(), _sessionItem, new Forms.ToolStripSeparator(),
            _topmostItem, _lockItem, rescan, settings, data, new Forms.ToolStripSeparator(), exit]);
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Chest Counter",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Ui(_app.ToggleWidget);
        };

        _app.Host.Stats.Invalidated += _ => Ui(UpdateTooltip);
        UpdateTooltip();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void Ui(Action action)
    {
        if (Application.Current?.Dispatcher is { } d)
        {
            d.BeginInvoke(action);
        }
    }

    private void RefreshMenu()
    {
        var s = _app.Host.Settings.Current;
        _showItem.Text = _app.WidgetVisible ? "Hide widget" : "Show widget";
        _topmostItem.Checked = s.WidgetTopmost;
        _lockItem.Checked = s.WidgetLocked;
        _sessionItem.Text = _app.Host.Stats.GetSnapshot().ActiveSession is not null ? "Stop session" : "Start session";
    }

    private void UpdateTooltip()
    {
        try
        {
            var snap = _app.Host.Stats.GetSnapshot();
            var text = $"Chest Counter – {snap.Overall.TotalCredited.ToString("N0", CultureInfo.CurrentCulture)} chests";
            if (snap.ActiveSession is { } session)
            {
                text += $" · session {session.Chests.ToString("N0", CultureInfo.CurrentCulture)}";
            }
            _icon.Text = text.Length > 120 ? text[..120] : text;
        }
        catch
        {
            // tooltip is cosmetic
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
