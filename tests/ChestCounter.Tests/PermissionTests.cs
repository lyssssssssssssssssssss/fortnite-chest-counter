using ChestCounter.Core;
using ChestCounter.Core.Settings;

namespace ChestCounter.Tests;

/// <summary>First-run permission: nothing in the replay folder is read until the user allows it.</summary>
[Collection("integration")]
public class PermissionTests
{
    [Fact]
    public void Fresh_install_reads_nothing_until_allowed()
    {
        var real = RealReplays.Complete(1);
        if (real.Count == 0) return;
        using var data = new TempDir();
        using var demos = new TempDir();
        File.Copy(real[0], demos.File("a.replay"));

        using var host = new AppHost(data.Path);
        Assert.False(host.Settings.Current.ReplayAccessGranted); // a mate's first run
        host.Settings.Update(s => { s.ReplayFolder = demos.Path; s.WebPort = 49950 + Random.Shared.Next(0, 40); });

        host.Start();
        Thread.Sleep(1500);
        host.Library.ScanNowForTests(); // even a direct scan must refuse

        Assert.Empty(host.Database.GetReplayFiles());
        Assert.Empty(host.Database.GetMatches());
        var snap = host.Stats.GetSnapshot();
        Assert.False(snap.Library.AccessGranted);
        Assert.Contains("permission", snap.Library.Phase, StringComparison.OrdinalIgnoreCase);

        // The user clicks "Allow and start": reading begins.
        host.GrantReplayAccess(demos.Path, startWithWindows: false);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (host.Database.GetMatches().Count == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
        }
        Assert.Single(host.Database.GetMatches());
        Assert.True(host.Settings.Current.ReplayAccessGranted);
        Assert.False(host.Settings.Current.StartWithWindows); // their choice in the prompt is kept
        Assert.NotNull(host.Settings.Current.ReplayAccessGrantedUtc);
    }

    [Fact]
    public void Withdrawing_permission_stops_reading_and_autostart()
    {
        var real = RealReplays.Complete(2);
        if (real.Count < 2) return;
        using var data = new TempDir();
        using var demos = new TempDir();
        File.Copy(real[1], demos.File("a.replay"));

        using var host = new AppHost(data.Path);
        host.Settings.Update(s => { s.ReplayAccessGranted = true; s.ReplayFolder = demos.Path; });
        host.Library.ScanNowForTests();
        Assert.Single(host.Database.GetMatches());

        host.RevokeReplayAccess();
        Assert.False(host.Settings.Current.ReplayAccessGranted);
        Assert.False(host.Settings.Current.StartWithWindows);

        File.Copy(real[0], demos.File("b.replay"));
        host.Library.ScanNowForTests();
        Assert.Single(host.Database.GetMatches()); // the new replay was not read
    }

    [Fact]
    public void Existing_installs_keep_their_permission_after_the_update()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "settingsVersion": 2, "startWithWindows": true }""");
        var s = new SettingsStore(path).Current;
        Assert.True(s.ReplayAccessGranted);
        Assert.Equal(AppSettings.CurrentSettingsVersion, s.SettingsVersion);
    }

    [Fact]
    public void New_installs_must_ask()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.File("settings.json")).Current;
        Assert.False(s.ReplayAccessGranted);
        Assert.Null(s.ReplayAccessGrantedUtc);
    }
}
