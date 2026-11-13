namespace ChestCounter.Core;

/// <summary>Where the app keeps its own files. Everything lives in one per-user folder.</summary>
public sealed class AppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
            ?? Environment.GetEnvironmentVariable("CHESTCOUNTER_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChestCounter");
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "chestcounter.db");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string LogPath => Path.Combine(DataDirectory, "chestcounter.log");

    /// <summary>The default Fortnite replay folder: %LOCALAPPDATA%\FortniteGame\Saved\Demos.</summary>
    public static string DefaultReplayFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");
}
