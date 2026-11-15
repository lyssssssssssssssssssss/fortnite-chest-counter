using System.Text.Json;
using ChestCounter.Core.Analysis;
using ChestCounter.ReplayReader;

// Usage: ChestCounter.Probe --dir <demos folder> [--count N] [--skip N] [--out <folder>] [--file <replay>]
//        [--analyze [--verbose]] [--treasures]
var argsList = args.ToList();
string? Arg(string name) { var i = argsList.IndexOf(name); return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : null; }

var dir = Arg("--dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");
var count = int.TryParse(Arg("--count"), out var c) ? c : 5;
var skip = int.TryParse(Arg("--skip"), out var s) ? s : 0;
var outDir = Arg("--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "probe-out");
var single = Arg("--file");
Directory.CreateDirectory(outDir);

var files = single is not null
    ? new[] { single }
    : Directory.GetFiles(dir, "*.replay").OrderByDescending(File.GetLastWriteTimeUtc).Skip(skip).Take(count).ToArray();

if (argsList.Contains("--treasures"))
{
    // Treasure hunt: each treasure received, who it was attributed to, and the chest events around it.
    var ar = new FactsReplayReader();
    var an = new MatchAnalyzer();
    int mine = 0, team = 0, untracked = 0;
    foreach (var file in files)
    {
        var f = ar.Read(file);
        var m = an.Analyze(f);
        var rec = f.RecorderPlayerState ?? f.ControllerPlayerState;
        mine += m.Treasures;
        team += m.TeamTreasures;
        if (!m.TreasuresTracked && m.Eligibility == MatchEligibility.Eligible) untracked++;
        Console.WriteLine($"{Path.GetFileNameWithoutExtension(file)} | {m.Playlist} | {m.Eligibility} | len={f.LengthMs / 1000}s listUpdates={f.AbilityListUpdates} err={f.AbilityListErrors} " +
            $"largest(me)={(rec is uint r0 ? f.AbilityListLargestUpdate.GetValueOrDefault(r0) : -1)} tracked={m.TreasuresTracked} " +
            $"| treasures mine={m.Treasures} team={m.TeamTreasures} | chests {m.ChestsCredited}");
        foreach (var (g, find) in f.TreasureGrants.Where(g => g.Actor == rec).Zip(m.TreasureFinds))
        {
            var near = m.Events.Where(e => e.Time >= g.Time - 15 && e.Time <= g.Time + 3)
                .Select(e => $"{e.Time - g.Time:+0.0;-0.0}s {e.By} {e.Action} {e.ClassName.Split("_C_")[0]}");
            Console.WriteLine($"    t={g.Time,7:F1} {(find.Mine ? "MINE" : "team")} {g.Ability} | {string.Join(" ; ", near)}");
        }
        foreach (var u in f.ContainerUpdates.Where(u => u.ClassName.Contains("TreasureHunt", StringComparison.OrdinalIgnoreCase)))
        {
            var near = m.Events.Where(e => e.Time >= u.Time - 10 && e.Time <= u.Time + 2 && e.Kind.IsChest() && !e.ClassName.Contains("TreasureHunt"))
                .Select(e => $"{e.Time - u.Time:+0.0;-0.0}s {e.By} {e.Action} {e.ClassName.Split("_C_")[0]}");
            Console.WriteLine($"    golden t={u.Time:F1} id={u.ContainerId} first={u.FirstSeen} pawn={u.SearchingPawn} searched={u.AlreadySearched} destroyed={u.Destroyed} anim={u.SearchAnimationCount} xy=({u.X:F0},{u.Y:F0}) | {string.Join(" ; ", near)}");
        }
    }
    Console.WriteLine($"\ntotal: mine {mine}, team {team}; eligible matches whose replay did not record the treasure hunt: {untracked}");
    return;
}

if (argsList.Contains("--analyze"))
{
    AnalyzeMode.Run(files, argsList.Contains("--verbose"));
    return;
}

var json = new JsonSerializerOptions { WriteIndented = true };
var reader = new FactsReplayReader(diagnostics: true);
foreach (var file in files)
{
    var facts = reader.Read(file);
    var name = Path.GetFileNameWithoutExtension(file);
    File.WriteAllText(Path.Combine(outDir, name + ".json"), JsonSerializer.Serialize(facts, json));
    if (facts.Diagnostics is { } diag)
    {
        File.WriteAllLines(Path.Combine(outDir, name + ".paths.txt"), diag.AllPaths);
    }
    var rec = facts.RecorderPlayerState ?? facts.ControllerPlayerState;
    var myPawns = facts.PawnToPlayerState.Where(kv => kv.Value == rec).Select(kv => kv.Key).ToHashSet();
    var myItems = facts.PawnWeapons.Where(w => myPawns.Contains(w.Pawn))
        .Select(w => facts.WeaponActorItem.TryGetValue(w.WeaponActor, out var item) ? item : $"#{w.WeaponActor}")
        .Distinct().ToList();
    var line = $"{name} | t={facts.RecordedLocalTime:yyyy-MM-dd HH:mm} len={facts.LengthMs / 1000}s | {facts.Branch} | map={facts.MapPath} " +
        $"| playlist={string.Join(",", facts.Playlists)} | rec={facts.RecorderPlayerState} ctl={facts.ControllerPlayerState} pawns={facts.PawnToPlayerState.Count} " +
        $"| cupd={facts.ContainerUpdates.Count} ccls={facts.ContainerCloses.Count} | place={facts.Placement}/{facts.TotalPlayers} " +
        $"| ok={facts.Completed} err={facts.Error} | {facts.ParseDuration.TotalSeconds:F1}s";
    Console.WriteLine(line);
    File.AppendAllText(Path.Combine(outDir, "summary.txt"), line + Environment.NewLine + "    items: " + string.Join(", ", myItems) + Environment.NewLine);
}
