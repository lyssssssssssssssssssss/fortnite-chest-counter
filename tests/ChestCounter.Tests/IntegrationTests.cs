using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ChestCounter.Core;
using ChestCounter.Core.Analysis;
using ChestCounter.Core.Data;
using ChestCounter.ReplayReader;

namespace ChestCounter.Tests;

/// <summary>
/// Integration tests on REAL replay files from this PC (copied to a temp folder; originals are only read).
/// They are skipped automatically when no Fortnite replays are available.
/// </summary>
[Collection("integration")]
public class IntegrationTests
{
    [Fact]
    public void Real_replay_parses_with_recorder_playlist_and_containers()
    {
        var real = RealReplays.Complete(3);
        if (real.Count == 0) return;
        var reader = new FactsReplayReader();
        foreach (var path in real)
        {
            var f = reader.Read(path);
            Assert.True(f.Completed, $"{Path.GetFileName(path)}: {f.Error}");
            Assert.NotNull(f.RecordedLocalTime);
            Assert.NotEmpty(f.Playlists);
            Assert.NotNull(f.RecorderPlayerState);
            Assert.NotEmpty(f.PawnToPlayerState);
            if (f.Playlists[0].Contains("Playlist_", StringComparison.Ordinal) && f.MapPath?.Contains("Delilah") == true)
            {
                Assert.NotEmpty(f.ContainerUpdates);
            }
        }
    }

    [Fact]
    public void Real_replay_ability_lists_decode_without_errors()
    {
        // The treasure count depends on decoding the recorder's ability list (treasure handler grants).
        var real = RealReplays.Complete(5);
        if (real.Count == 0) return;
        var reader = new FactsReplayReader();
        foreach (var path in real)
        {
            var f = reader.Read(path);
            if (f.MapPath?.Contains("Delilah") != true) continue; // this season's map
            Assert.True(f.AbilityListUpdates > 0, $"{Path.GetFileName(path)}: no ability list found");
            Assert.Equal(0, f.AbilityListErrors);
            Assert.All(f.TreasureGrants, g => Assert.Equal(f.RecorderPlayerState, g.Actor));
        }
    }

    [Fact]
    public void Library_scan_handles_duplicates_cutoff_and_restart()
    {
        var real = RealReplays.Complete(3);
        if (real.Count < 2) return;

        using var data = new TempDir();
        using var demos = new TempDir();
        foreach (var r in real) File.Copy(r, demos.File(Path.GetFileName(r)));
        File.Copy(real[0], demos.File("My saved copy.replay"));             // duplicate of a real match
        File.WriteAllBytes(demos.File("UnsavedReplay-broken.replay"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33]);
        File.WriteAllBytes(demos.File("UnsavedReplay-recording.replay"), []); // match in progress

        int storedMatches;
        int credited;
        using (var host = new AppHost(data.Path))
        {
            host.Settings.Update(s => { s.ReplayAccessGranted = true; s.ReplayFolder = demos.Path; s.SeasonStartDate = "2026-11-01"; });
            host.Library.ScanNowForTests();

            var files = host.Database.GetReplayFiles();
            Assert.Equal(ReplayFileStatus.Duplicate, files[demos.File("My saved copy.replay")].Status);
            Assert.Equal(ReplayFileStatus.Invalid, files[demos.File("UnsavedReplay-broken.replay")].Status);
            Assert.Equal(ReplayFileStatus.Incomplete, files[demos.File("UnsavedReplay-recording.replay")].Status);
            storedMatches = host.Database.GetMatches().Count;
            Assert.Equal(real.Count, storedMatches);
            Assert.Equal("1", host.Database.GetMeta("initial_import_done"));
            credited = host.Stats.GetSnapshot().Overall.TotalCredited;

            // Rescan: nothing is re-analysed or double counted
            host.Library.ScanNowForTests();
            Assert.Equal(storedMatches, host.Database.GetMatches().Count);
        }

        // Restart: same numbers come back from disk
        using (var host = new AppHost(data.Path))
        {
            Assert.Equal(credited, host.Stats.GetSnapshot().Overall.TotalCredited);

            // Moving the season start after these matches removes them from every statistic
            host.Settings.Update(s => s.SeasonStartDate = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd"));
            var snap = host.Stats.GetSnapshot();
            Assert.Equal(0, snap.Overall.TotalCredited);
            Assert.Equal(0, snap.Overall.Matches);
        }
    }

    [Fact]
    public void Replays_before_the_season_start_are_not_even_parsed()
    {
        var real = RealReplays.Complete(1);
        if (real.Count == 0) return;
        using var data = new TempDir();
        using var demos = new TempDir();
        File.Copy(real[0], demos.File("a.replay"));
        using var host = new AppHost(data.Path);
        host.Settings.Update(s => { s.ReplayAccessGranted = true; s.ReplayFolder = demos.Path; s.SeasonStartDate = DateTime.Now.AddDays(2).ToString("yyyy-MM-dd"); });
        host.Library.ScanNowForTests();
        Assert.Equal(ReplayFileStatus.BeforeCutoff, host.Database.GetReplayFiles()[demos.File("a.replay")].Status);
        Assert.Empty(host.Database.GetMatches());
    }

    [Fact]
    public async Task Dashboard_api_matches_the_widget_data_and_rejects_foreign_requests()
    {
        var real = RealReplays.Complete(2);
        using var data = new TempDir();
        using var demos = new TempDir();
        foreach (var r in real) File.Copy(r, demos.File(Path.GetFileName(r)));

        using var host = new AppHost(data.Path);
        host.Settings.Update(s => { s.ReplayAccessGranted = true; s.ReplayFolder = demos.Path; s.WebPort = 48900 + Random.Shared.Next(0, 90); });
        host.Library.ScanNowForTests();
        host.Web.Start(host.Settings.Current.WebPort);

        using var http = new HttpClient { BaseAddress = new Uri(host.Web.Url) };

        // Same numbers in the API (dashboard) and in-process (widget)
        var widget = host.Stats.GetSnapshot();
        using var doc = JsonDocument.Parse(await http.GetStringAsync("api/snapshot"));
        var overall = doc.RootElement.GetProperty("overall");
        Assert.Equal(widget.Overall.TotalCredited, overall.GetProperty("totalCredited").GetInt32());
        Assert.Equal(widget.Overall.Matches, overall.GetProperty("matches").GetInt32());
        Assert.Equal(widget.Overall.Cpg, overall.GetProperty("cpg").GetDouble());

        // Dashboard page and assets are served
        Assert.Contains("Chest Counter", await http.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("app.js")).StatusCode);

        // State-changing calls require the app's header (blocks cross-site requests)
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("api/session/start", null)).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "api/session/start");
        req.Headers.Add("X-Chest-Counter", "1");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(req)).StatusCode);
        Assert.NotNull(host.Stats.GetSnapshot().ActiveSession);

        // A session started from the website is visible to the widget immediately (same database)
        using var doc2 = JsonDocument.Parse(await http.GetStringAsync("api/snapshot"));
        Assert.Equal(JsonValueKind.Object, doc2.RootElement.GetProperty("activeSession").ValueKind);

        var stop = new HttpRequestMessage(HttpMethod.Post, "api/session/stop");
        stop.Headers.Add("X-Chest-Counter", "1");
        await http.SendAsync(stop);
        Assert.Null(host.Stats.GetSnapshot().ActiveSession);

        // DNS-rebinding protection: a foreign Host header is refused
        var foreign = new HttpRequestMessage(HttpMethod.Get, "api/snapshot");
        foreign.Headers.Host = "evil.example";
        var foreignResponse = await http.SendAsync(foreign);
        Assert.NotEqual(HttpStatusCode.OK, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task Live_update_event_is_pushed_when_data_changes()
    {
        using var data = new TempDir();
        using var host = new AppHost(data.Path);
        host.Settings.Update(s => { s.ReplayAccessGranted = true; s.ReplayFolder = data.Path; s.WebPort = 48990 + Random.Shared.Next(0, 9); });
        host.Web.Start(host.Settings.Current.WebPort);

        using var http = new HttpClient { BaseAddress = new Uri(host.Web.Url), Timeout = TimeSpan.FromSeconds(20) };
        using var resp = await http.GetAsync("api/events", HttpCompletionOption.ResponseHeadersRead);
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // first message = current version
        string? line;
        do { line = await reader.ReadLineAsync(); } while (line is not null && !line.StartsWith("data:"));
        var before = long.Parse(line!["data:".Length..].Trim());

        host.StartSession(); // any data change invalidates the snapshot and pushes an update

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        long after = before;
        while (!cts.IsCancellationRequested && after == before)
        {
            line = await reader.ReadLineAsync(cts.Token);
            if (line?.StartsWith("data:") == true) after = long.Parse(line["data:".Length..].Trim());
        }
        Assert.True(after > before);
    }

    [Fact]
    public void Gum_gum_destruction_is_detected_in_a_real_match_when_available()
    {
        // Real match from 7 Nov 2026 where the recorder used the Gum Gum fruit (only while Fortnite still keeps it).
        var path = Path.Combine(RealReplays.Folder, "UnsavedReplay-2026.11.07-15.37.39.replay");
        if (!File.Exists(path)) return;
        var m = new MatchAnalyzer().Analyze(new FactsReplayReader().Read(path));
        Assert.Equal(MatchEligibility.Eligible, m.Eligibility);
        Assert.True(m.GumGumChestsDestroyed >= 5, $"gum gum chests: {m.GumGumChestsDestroyed}");
        Assert.True(m.ChestsDestroyed >= m.GumGumChestsDestroyed);
    }
}
