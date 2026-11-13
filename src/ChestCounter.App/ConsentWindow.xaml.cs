using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChestCounter.Core;
using ChestCounter.Core.Library;
using Microsoft.Win32;

namespace ChestCounter.App;

/// <summary>
/// First-run permission prompt. Shown before anything in the replay folder is read; only checks whether the
/// folder exists (it does not list or open any file) until the user clicks "Allow".
/// </summary>
public partial class ConsentWindow : Window
{
    private string? _chosenFolder;

    public ConsentWindow(AppHost host)
    {
        InitializeComponent();
        _chosenFolder = host.Settings.Current.ReplayFolder;
        UpdateFolder();
    }

    /// <summary>The folder the user confirmed (null = Fortnite's default location).</summary>
    public string? ChosenFolder => _chosenFolder;

    public bool StartWithWindows => StartupBox.IsChecked == true;

    private void UpdateFolder()
    {
        var folder = ReplayLibrary.ResolveReplayFolder(_chosenFolder);
        FolderText.Text = folder;
        if (Directory.Exists(folder))
        {
            FolderStatus.Text = "✓ Folder found";
            FolderStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0xCB, 0x7F));
        }
        else
        {
            FolderStatus.Text = "Not found on this PC yet. If Fortnite saves replays somewhere else, choose the folder with Change… (it is normally …\\FortniteGame\\Saved\\Demos).";
            FolderStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x3C));
        }
    }

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var current = ReplayLibrary.ResolveReplayFolder(_chosenFolder);
        var dlg = new OpenFolderDialog
        {
            Title = "Choose your Fortnite replay folder (…\\FortniteGame\\Saved\\Demos)",
            InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        if (dlg.ShowDialog(this) == true)
        {
            _chosenFolder = string.Equals(dlg.FolderName, AppPaths.DefaultReplayFolder, StringComparison.OrdinalIgnoreCase) ? null : dlg.FolderName;
            UpdateFolder();
        }
    }

    private void OnAllow(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDeny(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Renders the window to a PNG (for previews / documentation).</summary>
    public void RenderToPng(string path, double scale)
    {
        UpdateLayout();
        var w = (int)Math.Ceiling(Root.ActualWidth * scale);
        var h = (int)Math.Ceiling(Root.ActualHeight * scale);
        var bmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(Root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
