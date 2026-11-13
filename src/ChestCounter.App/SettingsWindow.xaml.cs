using System.Globalization;
using System.Windows;
using ChestCounter.Core;
using ChestCounter.Core.Library;
using Microsoft.Win32;

namespace ChestCounter.App;

public partial class SettingsWindow : Window
{
    private readonly App _app;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        var s = _app.Host.Settings.Current;
        var snap = _app.Host.Stats.GetSnapshot();

        SeasonDate.SelectedDate = s.SeasonStartLocal;
        PickaxeBox.IsChecked = s.ExcludePickaxeOnlyMatches;
        FolderBox.Text = s.ReplayFolder ?? "";
        UpdateFolderHint();
        NameBox.Text = s.DisplayName;
        AccountBox.Text = s.AccountFilter;
        AccountHint.Text = $"\"auto\" counts the account that recorded most of your replays (currently {snap.Library.Account ?? "unknown"}). Enter a specific account id to count only that account.";
        GumGumBox.Text = string.Join(", ", s.GumGumItemIds);
        EligibleBox.Text = string.Join(", ", s.ExtraEligiblePlaylists);
        ExcludedBox.Text = string.Join(", ", s.ExtraExcludedPlaylists);
        PortBox.Text = s.WebPort.ToString(CultureInfo.InvariantCulture);
        PortHint.Text = $"Currently serving {snap.App.DashboardUrl} (only reachable from this PC). Port changes apply after restarting the app.";
        TopmostBox.IsChecked = s.WidgetTopmost;
        StartupBox.IsChecked = s.StartWithWindows;
        OpenDashBox.IsChecked = s.OpenDashboardOnStart;
        AboutText.Text = $"Chest Counter {AppHost.Version} · data folder: {_app.Host.Paths.DataDirectory}\n" +
                         "Reads Fortnite replay files only (read-only). It never touches the game process, its memory, its files or its network traffic.";
        FolderBox.TextChanged += (_, _) => UpdateFolderHint();

        var since = DateTime.TryParse(s.ReplayAccessGrantedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var g)
            ? $" since {g.ToLocalTime():d MMM yyyy}"
            : "";
        AccessText.Text = s.ReplayAccessGranted
            ? $"Allowed{since}. Chest Counter only reads the .replay files in your replay folder, read-only, and nothing leaves this PC."
            : "Not allowed: nothing is being read.";
    }

    private void OnRevoke(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Chest Counter will stop reading your replays, stop starting with Windows, and close.\n\n" +
            "Your saved stats stay on this PC (the uninstaller can delete them). It will ask for permission again the next time you open it.\n\nWithdraw permission?",
            "Chest Counter", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _app.Host.RevokeReplayAccess();
        StartupRegistration.Apply(false);
        Close();
        _app.ExitApp("permission to read the replay folder was withdrawn");
    }

    private void UpdateFolderHint()
    {
        var resolved = ReplayLibrary.ResolveReplayFolder(FolderBox.Text);
        var exists = Directory.Exists(resolved);
        var count = 0;
        try { if (exists) count = Directory.EnumerateFiles(resolved, "*.replay").Count(); } catch { }
        FolderHint.Text = string.IsNullOrWhiteSpace(FolderBox.Text)
            ? $"Auto-detected: {resolved} — {(exists ? $"{count} replay files" : "not found")}"
            : exists ? $"{count} replay files found." : "This folder does not exist.";
    }

    private static List<string> SplitList(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (SeasonDate.SelectedDate is not DateTime season)
        {
            MessageBox.Show(this, "Please choose a season start date.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
        {
            MessageBox.Show(this, "The port must be a number between 1024 and 65535.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var folder = FolderBox.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(Environment.ExpandEnvironmentVariables(folder)))
        {
            if (MessageBox.Show(this, "That replay folder does not exist. Save anyway?", "Settings", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var gum = SplitList(GumGumBox.Text);
        var startup = StartupBox.IsChecked == true;
        _app.Host.Settings.Update(s =>
        {
            s.SeasonStartDate = season.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            s.ExcludePickaxeOnlyMatches = PickaxeBox.IsChecked == true;
            s.ReplayFolder = folder.Length > 0 ? folder : null;
            s.DisplayName = NameBox.Text.Trim();
            s.AccountFilter = string.IsNullOrWhiteSpace(AccountBox.Text) ? "auto" : AccountBox.Text.Trim();
            s.GumGumItemIds = gum.Count > 0 ? gum : ["WID_TackyGoofiness"];
            s.ExtraEligiblePlaylists = SplitList(EligibleBox.Text);
            s.ExtraExcludedPlaylists = SplitList(ExcludedBox.Text);
            s.WebPort = port;
            s.WidgetTopmost = TopmostBox.IsChecked == true;
            s.StartWithWindows = startup;
            s.OpenDashboardOnStart = OpenDashBox.IsChecked == true;
        });
        StartupRegistration.Apply(startup);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose the Fortnite replay folder (…\\FortniteGame\\Saved\\Demos)",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : AppPaths.DefaultReplayFolder,
        };
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
        }
    }

    private void OnAutoFolder(object sender, RoutedEventArgs e) => FolderBox.Text = "";

    private void OnRescan(object sender, RoutedEventArgs e)
    {
        _app.Host.Library.RequestRescan();
        MessageBox.Show(this, "Rescan started. New replays will appear in a few seconds.", "Chest Counter", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnReanalyze(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Analyse every replay still on disk again? Matches whose replay files were already deleted by Fortnite keep their stored results.",
                "Chest Counter", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            _app.Host.Library.RequestReanalyzeAll();
        }
    }

    private void OnDataFolder(object sender, RoutedEventArgs e) => _app.OpenDataFolder();
}
