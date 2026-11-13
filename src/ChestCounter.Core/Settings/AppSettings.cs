using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChestCounter.Core.Settings;

/// <summary>User settings, stored as settings.json in the data folder.</summary>
public sealed class AppSettings
{
    /// <summary>Season start: matches played (by the date recorded inside the replay) before this local date are ignored everywhere.</summary>
    public string SeasonStartDate { get; set; } = "2026-11-01";

    /// <summary>Maximum number of eligible matches imported by the first historical scan (most recent first).</summary>
    public int InitialMatchLimit { get; set; } = 100;

    /// <summary>Replay folder; null or empty = auto-detect (%LOCALAPPDATA%\FortniteGame\Saved\Demos).</summary>
    public string? ReplayFolder { get; set; }

    /// <summary>Name shown in the dashboard header (Fortnite obfuscates names inside replays).</summary>
    public string DisplayName { get; set; } = Environment.UserName;

    /// <summary>Port of the local dashboard (http://localhost:PORT). Only reachable from this PC.</summary>
    public int WebPort { get; set; } = 48231;

    /// <summary>Epic account whose matches are counted; "auto" = the account that recorded most replays.</summary>
    public string AccountFilter { get; set; } = "auto";

    /// <summary>Item definitions identifying the Gum Gum Devil Fruit (Fortnite codename: TackyGoofiness).</summary>
    public List<string> GumGumItemIds { get; set; } = ["WID_TackyGoofiness"];

    /// <summary>
    /// Don't count matches where you only ever held a pickaxe and got no chests (AFK, spectating, or eliminated
    /// before looting): they would otherwise lower your chests-per-match average.
    /// </summary>
    public bool ExcludePickaxeOnlyMatches { get; set; } = true;

    /// <summary>Extra playlist names (exact, case-insensitive) to treat as eligible public Battle Royale.</summary>
    public List<string> ExtraEligiblePlaylists { get; set; } = [];

    /// <summary>Extra playlist names (exact, case-insensitive) to always exclude.</summary>
    public List<string> ExtraExcludedPlaylists { get; set; } = [];

    // ---- Widget ----
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public double WidgetWidth { get; set; } = 300;
    public double WidgetHeight { get; set; } = 264;
    public bool WidgetTopmost { get; set; } = true;
    public bool WidgetLocked { get; set; }
    public double WidgetOpacity { get; set; } = 1.0;
    public bool WidgetVisible { get; set; } = true;

    /// <summary>
    /// Start quietly in the tray when you sign in to Windows, so no match is ever missed (on by default since
    /// settings version 2; it can be turned off in Settings).
    /// </summary>
    public bool StartWithWindows { get; set; } = true;
    public bool OpenDashboardOnStart { get; set; }

    /// <summary>
    /// The user allowed Chest Counter to read their Fortnite replay folder. Asked on first run; nothing in the
    /// replay folder is read until this is true. Can be withdrawn in Settings.
    /// </summary>
    public bool ReplayAccessGranted { get; set; }

    /// <summary>When permission was given (UTC, ISO 8601).</summary>
    public string? ReplayAccessGrantedUtc { get; set; }

    /// <summary>Settings schema version (used for one-time upgrades of older settings files).</summary>
    public int SettingsVersion { get; set; }

    public const int CurrentSettingsVersion = 3;

    [JsonIgnore]
    public DateTime SeasonStartLocal =>
        DateTime.TryParseExact(SeasonStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : new DateTime(2026, 11, 1);

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings, AppSettings>? Changed; // (old, new)

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
                if (s is not null)
                {
                    var upgraded = Upgrade(s);
                    Normalize(s);
                    if (upgraded)
                    {
                        Save(s);
                    }
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings file unreadable, using defaults: {ex.Message}");
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { }
        }

        var fresh = new AppSettings { SettingsVersion = AppSettings.CurrentSettingsVersion };
        Save(fresh);
        return fresh;
    }

    /// <summary>One-time upgrades of settings files written by older versions. Returns true when something changed.</summary>
    internal static bool Upgrade(AppSettings s)
    {
        if (s.SettingsVersion >= AppSettings.CurrentSettingsVersion)
        {
            return false;
        }

        if (s.SettingsVersion < 2)
        {
            // v2 "never miss a match": always run in the background so replays are read before Fortnite deletes them.
            s.StartWithWindows = true;
        }

        if (s.SettingsVersion < 3)
        {
            // v3 asks permission before reading the replay folder. Installs made before v3 were already reading it
            // with their owner's agreement, so they keep that permission instead of being asked again.
            s.ReplayAccessGranted = true;
            s.ReplayAccessGrantedUtc ??= DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        s.SettingsVersion = AppSettings.CurrentSettingsVersion;
        return true;
    }

    private static void Normalize(AppSettings s)
    {
        s.InitialMatchLimit = Math.Clamp(s.InitialMatchLimit, 1, 10_000);
        s.WebPort = s.WebPort is >= 1024 and <= 65535 ? s.WebPort : 48231;
        s.WidgetOpacity = Math.Clamp(s.WidgetOpacity, 0.3, 1.0);
        s.WidgetWidth = Math.Clamp(s.WidgetWidth, 220, 900);
        s.WidgetHeight = Math.Clamp(s.WidgetHeight, 150, 700);
        s.GumGumItemIds ??= ["WID_TackyGoofiness"];
        s.ExtraEligiblePlaylists ??= [];
        s.ExtraExcludedPlaylists ??= [];
        if (string.IsNullOrWhiteSpace(s.DisplayName)) s.DisplayName = Environment.UserName;
        if (string.IsNullOrWhiteSpace(s.AccountFilter)) s.AccountFilter = "auto";
        if (!DateTime.TryParseExact(s.SeasonStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            s.SeasonStartDate = "2026-11-01";
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings old, updated;
        lock (_gate)
        {
            old = Current;
            updated = Current.Clone();
            mutate(updated);
            Normalize(updated);
            Save(updated);
            Current = updated;
        }
        Changed?.Invoke(old, updated);
    }

    private void Save(AppSettings s)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
