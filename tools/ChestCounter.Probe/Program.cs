using System.Text.Json;
using ChestCounter.ReplayReader;

// Usage: ChestCounter.Probe --dir <demos folder> [--count N] [--skip N] [--out <folder>] [--file <replay>]
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
