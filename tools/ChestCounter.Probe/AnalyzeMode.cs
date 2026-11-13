using ChestCounter.Core.Analysis;
using ChestCounter.ReplayReader;

internal static class AnalyzeMode
{
    /// <summary>Runs the real analyzer over replays and prints per-match results plus rule diagnostics.</summary>
    public static void Run(IEnumerable<string> files, bool verbose)
    {
        var reader = new FactsReplayReader();
        var analyzer = new MatchAnalyzer();
        var byClass = new Dictionary<string, int[]>(); // class -> [me open, me destroy, team, other, unknown]
        int destroyedSpNoAnim = 0, openedNoAnim = 0, unwitnessedMine = 0, totalMine = 0;

        foreach (var file in files)
        {
            var facts = reader.Read(file);
            var m = analyzer.Analyze(facts);

            Console.WriteLine($"{Path.GetFileNameWithoutExtension(file)} | {m.MatchStartLocal:MM-dd HH:mm} | {m.Status} | {m.Eligibility,-12} | {m.Mode,-22} " +
                $"| opened {m.ChestsOpened,3} destroyed {m.ChestsDestroyed,3} credited {m.ChestsCredited,3} gumgum {m.GumGumChestsDestroyed,2} " +
                $"| ammo {m.MyCount(ContainerKind.AmmoBox),2} cannon {m.MyCount(ContainerKind.LootCannon),2} other {m.MyCount(ContainerKind.OtherContainer),2} " +
                $"| unattrDestroyed {m.UnattributedChestsDestroyed,2} (during gum {m.UnattributedDestroyedDuringGumGum}) | place {m.Placement}/{m.TotalPlayers} | {m.PlayerName} {m.AccountId}");

            if (verbose)
            {
                foreach (var e in m.Events.Where(e => e.By == Attribution.Me))
                {
                    Console.WriteLine($"      {e.Time,7:F1} {e.Action,-9} {e.Kind,-20} {e.ClassName,-40} gum={e.WithGumGum} witnessed={e.Witnessed}");
                }
            }

            foreach (var e in m.Events)
            {
                if (!byClass.TryGetValue(e.ClassName, out var c)) byClass[e.ClassName] = c = new int[5];
                var idx = e.By switch { Attribution.Me => e.Action == ChestAction.Opened ? 0 : 1, Attribution.Teammate => 2, Attribution.Other => 3, _ => 4 };
                c[idx]++;
                if (e.By == Attribution.Me) { totalMine++; if (!e.Witnessed) unwitnessedMine++; }
            }

            // raw rule diagnostics
            foreach (var u in facts.ContainerUpdates)
            {
                if (u.Destroyed == true && u.SearchingPawn is > 0 && (u.SearchAnimationCount ?? 0) == 0) destroyedSpNoAnim++;
                if (u.AlreadySearched == true && !u.FirstSeen && (u.SearchAnimationCount ?? 0) == 0) openedNoAnim++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("class                                              meOpen meDestr  team  other unknown  -> kind");
        foreach (var (cls, c) in byClass.OrderByDescending(kv => kv.Value.Sum()))
        {
            Console.WriteLine($"{cls,-50} {c[0],6} {c[1],6} {c[2],5} {c[3],6} {c[4],7}  -> {ContainerKinds.Classify(cls)}");
        }
        Console.WriteLine($"\nupdates destroyed+pawn but anim=0: {destroyedSpNoAnim}; opened(not first) with anim=0: {openedNoAnim}; my events unwitnessed: {unwitnessedMine}/{totalMine}");
    }
}
