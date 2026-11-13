using ChestCounter.Core;
using ChestCounter.Core.Data;
using ChestCounter.Core.Library;
using ChestCounter.Core.Settings;

namespace ChestCounter.Tests;

/// <summary>"Never miss a match": autostart default, clean-exit tracking, catch-up and missed-replay detection.</summary>
[Collection("integration")]
public class NeverMissTests
{
    [Fact]
    public void Old_settings_files_are_upgraded_to_start_with_windows()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{ "seasonStartDate": "2026-11-01", "startWithWindows": false, "widgetVisible": false }""");

        var s = new SettingsStore(path).Current;
        Assert.True(s.StartWithWindows);
        Assert.Equal(AppSettings.CurrentSettingsVersion, s.SettingsVersion);
        Assert.False(s.WidgetVisible); // other choices are kept
        Assert.Contains($"\"settingsVersion\": {AppSettings.CurrentSettingsVersion}", File.ReadAllText(path)); // upgrade is saved once

        // After the upgrade, turning it off is respected.
        var store = new SettingsStore(path);
        store.Update(x => x.StartWithWindows = false);
        Assert.False(new SettingsStore(path).Current.StartWithWindows);
    }

    [Fact]
    public void New_installs_start_with_windows()
    {
        using var dir = new TempDir();
        Assert.True(new SettingsStore(dir.File("settings.json")).Current.StartWithWindows);
    }

    private static AppHost QuietHost(TempDir data, string replayFolder)
    {
        var host = new AppHost(data.Path);
        host.Settings.Update(s =>
        {
            s.ReplayAccessGranted = true;
            s.ReplayFolder = replayFolder;
            s.WebPort = 49100 + Random.Shared.Next(0, 800);
        });
        return host;
    }

    [Fact]
    public void Clean_exits_and_forced_closes_are_told_apart()
    {
        using var data = new TempDir();
        using var empty = new TempDir();

        var first = QuietHost(data, empty.Path);
        first.Start();
        first.Stop("Exit from the tray menu");

        var db = new Database(data.File("chestcounter.db"));
        var afterClean = PreviousRunInfo.Read(db);
        Assert.True(afterClean.HadRun);
        Assert.True(afterClean.EndedCleanly);
        Assert.Equal("Exit from the tray menu", afterClean.LastStopReason);

        // Simulate "End task" in Task Manager: the process dies without Stop().
        var second = QuietHost(data, empty.Path);
        second.Start();
        second.Web.Dispose();
        second.Library.Dispose();
        var afterKill = PreviousRunInfo.Read(new Database(data.File("chestcounter.db")));
        Assert.False(afterKill.EndedCleanly);
        Assert.NotNull(afterKill.LastAliveUtc);

        var third = QuietHost(data, empty.Path);
        third.Start();
        Assert.False(third.PreviousRun!.EndedCleanly); // reported (and logged) at the next start
        third.Stop("test");
    }

    [Fact]
    public void Matches_played_while_closed_are_caught_up_and_announced()
    {
        var real = RealReplays.Complete(3);
        if (real.Count < 3) return;
        using var data = new TempDir();
        using var demos = new TempDir();
        File.Copy(real[0], demos.File(Path.GetFileName(real[0])));
        File.Copy(real[1], demos.File(Path.GetFileName(real[1])));

        using (var host = QuietHost(data, demos.Path))
        {
            host.Library.ScanNowForTests(); // first import: nothing is "caught up"
            Assert.DoesNotContain(host.Stats.GetSnapshot().Notices, n => n.Kind == "catchup");
        }

        // The app was closed; a match was played; the app starts again.
        File.Copy(real[2], demos.File(Path.GetFileName(real[2])));
        using (var host = QuietHost(data, demos.Path))
        {
            host.Library.ScanNowForTests();
            var notice = Assert.Single(host.Stats.GetSnapshot().Notices, n => n.Kind == "catchup");
            Assert.Equal(1, notice.Count);
            Assert.Equal(3, host.Database.GetMatches().Count);

            // Re-scanning does not count it again.
            host.Library.ScanNowForTests();
            Assert.Equal(1, Assert.Single(host.Stats.GetSnapshot().Notices, n => n.Kind == "catchup").Count);
        }
    }

    [Fact]
    public void Replays_deleted_before_they_were_read_are_flagged()
    {
        var real = RealReplays.Complete(2); // newest first
        if (real.Count < 2) return;
        using var data = new TempDir();
        using var demos = new TempDir();
        var older = demos.File("older.replay");
        File.Copy(real[1], older);

        using var host = QuietHost(data, demos.Path);
        host.Library.FortniteReplayCap = 1; // pretend the folder is "full" with 1 replay
        host.Library.ScanNowForTests();

        // Fortnite rotated: the replay we knew is gone and only a newer one is on disk.
        File.Delete(older);
        File.Copy(real[0], demos.File("newer.replay"));
        host.Library.ScanNowForTests();

        var gap = Assert.Single(host.Stats.GetSnapshot().Notices, n => n.Kind == "gap");
        Assert.Equal("warn", gap.Level);
    }

    [Fact]
    public void No_false_alarm_when_the_folder_still_reaches_back()
    {
        var real = RealReplays.Complete(2);
        if (real.Count < 2) return;
        using var data = new TempDir();
        using var demos = new TempDir();
        File.Copy(real[1], demos.File("older.replay"));

        using var host = QuietHost(data, demos.Path);
        host.Library.FortniteReplayCap = 1;
        host.Library.ScanNowForTests();
        File.Copy(real[0], demos.File("newer.replay")); // older one still there
        host.Library.ScanNowForTests();
        Assert.DoesNotContain(host.Stats.GetSnapshot().Notices, n => n.Kind == "gap");
    }

    [Fact]
    public void Old_notices_expire_and_autostart_hint_follows_the_setting()
    {
        using var data = new TempDir();
        using var empty = new TempDir();
        using var host = QuietHost(data, empty.Path);
        host.Database.SetMeta(StoredNotice.CatchUpKey, new StoredNotice(5, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddHours(-13)).ToJson());
        host.Database.SetMeta(StoredNotice.GapKey, new StoredNotice(0, DateTime.UtcNow.AddDays(-5), DateTime.UtcNow.AddDays(-4), DateTime.UtcNow.AddHours(-49)).ToJson());
        host.Stats.Invalidate();
        Assert.Empty(host.Stats.GetSnapshot().Notices);

        host.Settings.Update(s => s.StartWithWindows = false);
        Assert.Contains(host.Stats.GetSnapshot().Notices, n => n.Kind == "autostart_off");
    }
}
