using ChestCounter.Core.Analysis;
using ChestCounter.ReplayReader;

namespace ChestCounter.Tests;

/// <summary>
/// SYNTHETIC FIXTURES: these replay facts are hand-built to exercise each counting rule. They mirror the
/// property patterns observed in real Fortnite 43.00 replays but are not real match data.
/// </summary>
public class MatchAnalyzerTests
{
    private const uint MyPs = 100, MatePs = 200, OtherPs = 300;
    private const uint MyPawn = 10, MatePawn = 20, OtherPawn = 30;
    private const string Chest = "Tiered_Chest_Athena_C";

    private static ReplayFacts Facts(string playlist = "Playlist_NoBuildBR_Squad")
    {
        var f = new ReplayFacts
        {
            FilePath = "synthetic.replay",
            RecordedLocalTime = new DateTime(2026, 11, 9, 20, 0, 0),
            LengthMs = 900_000,
            Completed = true,
            RecorderPlayerState = MyPs,
            ReplayGuid = Guid.NewGuid().ToString("N"),
        };
        f.Playlists.Add(playlist);
        f.PawnToPlayerState[MyPawn] = MyPs;
        f.PawnToPlayerState[MatePawn] = MatePs;
        f.PawnToPlayerState[OtherPawn] = OtherPs;
        f.PlayerStateTeam[MyPs] = 6;
        f.PlayerStateTeam[MatePs] = 6;
        f.PlayerStateTeam[OtherPs] = 9;
        f.AbilityListLargestUpdate[MyPs] = 60; // the replay recorded my full ability list
        return f;
    }

    private static ContainerUpdate U(float t, string id, string cls = Chest, uint? sp = null, bool? s = null, bool? d = null, uint? a = null, bool first = false,
        bool dynamic = false, float? x = null, float? y = null) =>
        new(t, id, cls, dynamic, x, y, dynamic ? 100f : null, sp, s, d, a, first);

    private static MatchAnalysis Run(ReplayFacts f, AnalyzerOptions? o = null) => new MatchAnalyzer(o).Analyze(f);

    [Fact]
    public void Chest_opened_by_me_counts_once()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "chestA", first: true));
        f.ContainerUpdates.Add(U(20, "chestA", sp: MyPawn, s: true, a: 1));
        var m = Run(f);
        Assert.Equal(1, m.ChestsOpened);
        Assert.Equal(0, m.ChestsDestroyed);
        Assert.Equal(1, m.ChestsCredited);
    }

    [Fact]
    public void Resent_snapshots_of_an_open_chest_are_not_counted_again()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "chestA", first: true));
        f.ContainerUpdates.Add(U(20, "chestA", sp: MyPawn, s: true, a: 1));
        f.ContainerUpdates.Add(U(300, "chestA", sp: MyPawn, s: true, a: 1, first: true)); // channel re-sent full state
        f.ContainerUpdates.Add(U(500, "chestA", sp: MyPawn, s: true, a: 1, first: true));
        Assert.Equal(1, Run(f).ChestsCredited);
    }

    // ---- Treasure hunt (patterns from real 43.10 replays: the treasure arrives in the same frame as the chest
    //      event, or up to ~1.5 s away; teammates' treasures are shared with the whole team) ----------------

    private static void Open(ReplayFacts f, float t, string id, uint pawn)
    {
        f.ContainerUpdates.Add(U(t - 5, id, first: true));
        f.ContainerUpdates.Add(U(t, id, sp: pawn, s: true, a: 1));
    }

    private static void Treasure(ReplayFacts f, float t, int entry, uint actor = MyPs) =>
        f.TreasureGrants.Add(new TreasureGrant(t, actor, entry, "GA_TreasureHandler_Generic_C"));

    [Fact]
    public void A_treasure_from_my_chest_is_mine()
    {
        var f = Facts();
        Open(f, 145.5f, "chestA", MyPawn);
        Treasure(f, 145.5f, 153);
        var m = Run(f);
        Assert.Equal(1, m.Treasures);
        Assert.Equal(0, m.TeamTreasures);
    }

    [Fact]
    public void A_treasure_from_a_teammates_chest_is_the_teams()
    {
        var f = Facts();
        Open(f, 133.0f, "mine", MyPawn);      // 5.7 s before: too early to be the source
        Open(f, 139.0f, "mates", MatePawn);   // 0.3 s after: the source
        Treasure(f, 138.7f, 149);
        var m = Run(f);
        Assert.Equal(0, m.Treasures);
        Assert.Equal(1, m.TeamTreasures);
    }

    [Fact]
    public void A_treasure_with_no_chest_of_mine_nearby_is_the_teams()
    {
        // A teammate far away (not in my replay) found it: my own chests are always in my replay.
        var f = Facts();
        Open(f, 100f, "far", OtherPawn);
        Treasure(f, 101f, 7);
        var m = Run(f);
        Assert.Equal(0, m.Treasures);
        Assert.Equal(1, m.TeamTreasures);
    }

    [Fact]
    public void An_equally_close_teammate_chest_wins_so_my_count_is_never_inflated()
    {
        var f = Facts();
        Open(f, 50f, "mine", MyPawn);
        Open(f, 50f, "mates", MatePawn);
        Treasure(f, 50f, 1);
        Assert.Equal(0, Run(f).Treasures);
    }

    [Fact]
    public void A_treasure_from_a_chest_i_broke_is_mine_and_resends_are_not_counted_twice()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(190f, "chestB", first: true));
        f.ContainerUpdates.Add(U(191f, "chestB", sp: MyPawn, d: true, a: 1));
        Treasure(f, 191f, 190);
        Open(f, 245f, "chestC", MyPawn);
        Treasure(f, 244.6f, 237);
        var m = Run(f);
        Assert.Equal(2, m.Treasures);
        Assert.Equal(2, m.TreasureFinds.Count);
    }

    [Fact]
    public void Treasures_are_unknown_not_zero_when_the_replay_did_not_record_my_ability_list()
    {
        // Seen in real replays: only the list's removal at the end of the match was recorded (0 entries added).
        var f = Facts();
        f.AbilityListLargestUpdate[MyPs] = 0;
        Open(f, 10f, "chestA", MyPawn);
        var m = Run(f);
        Assert.False(m.TreasuresTracked);
        Assert.Equal(0, m.Treasures);

        f.AbilityListLargestUpdate[MyPs] = 60;
        Assert.True(Run(f).TreasuresTracked);
    }

    [Fact]
    public void Treasures_of_another_players_ability_list_are_ignored()
    {
        var f = Facts();
        Open(f, 10f, "chestA", MyPawn);
        Treasure(f, 10f, 1, actor: MatePs);
        var m = Run(f);
        Assert.Equal(0, m.Treasures);
        Assert.Equal(0, m.TeamTreasures);
    }

    [Fact]
    public void The_golden_prize_chest_is_not_a_chest()
    {
        Assert.Equal(ContainerKind.Ignored, ContainerKinds.Classify("BGA_DelilahTreasureHuntAnimatedChest_C"));
        var f = Facts();
        f.ContainerUpdates.Add(U(109.9f, "dyn#15184", "BGA_DelilahTreasureHuntAnimatedChest_C", first: true, dynamic: true, x: 1, y: 1));
        f.ContainerUpdates.Add(U(111.7f, "dyn#15184", "BGA_DelilahTreasureHuntAnimatedChest_C", sp: MyPawn, d: true, a: 1, dynamic: true, x: 1, y: 1));
        var m = Run(f);
        Assert.Equal(0, m.ChestsCredited);
        Assert.Empty(m.Events);
    }

    [Fact]
    public void Teammate_and_enemy_chests_are_not_mine()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "a", first: true));
        f.ContainerUpdates.Add(U(11, "a", sp: MatePawn, s: true, a: 1));
        f.ContainerUpdates.Add(U(10, "b", first: true));
        f.ContainerUpdates.Add(U(12, "b", sp: OtherPawn, s: true, a: 1));
        var m = Run(f);
        Assert.Equal(0, m.ChestsCredited);
        Assert.Equal(1, m.Events.Count(e => e.By == Attribution.Teammate));
        Assert.Equal(1, m.Events.Count(e => e.By == Attribution.Other));
    }

    [Fact]
    public void Chest_destroyed_by_me_counts_as_destroyed()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "a", first: true));
        f.ContainerUpdates.Add(U(30, "a", sp: MyPawn, d: true, a: 1));
        var m = Run(f);
        Assert.Equal(0, m.ChestsOpened);
        Assert.Equal(1, m.ChestsDestroyed);
        Assert.Equal(1, m.ChestsCredited);
    }

    [Fact]
    public void Opened_then_destroyed_chest_is_counted_once_as_opened()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "a", first: true));
        f.ContainerUpdates.Add(U(20, "a", sp: MyPawn, s: true, a: 1));
        f.ContainerUpdates.Add(U(60, "a", sp: MyPawn, d: true));
        var m = Run(f);
        Assert.Equal(1, m.ChestsOpened);
        Assert.Equal(0, m.ChestsDestroyed);
    }

    [Fact]
    public void Destruction_without_a_culprit_is_never_credited()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "a", first: true));
        f.ContainerUpdates.Add(U(30, "a", d: true));                    // nobody on record
        f.ContainerUpdates.Add(U(10, "b", first: true));
        f.ContainerUpdates.Add(U(31, "b", sp: MyPawn, d: true, a: 0));  // interrupted search, no loot spill recorded
        var m = Run(f);
        Assert.Equal(0, m.ChestsCredited);
        Assert.Equal(2, m.UnattributedChestsDestroyed);
    }

    [Fact]
    public void Refilled_chest_can_be_counted_again()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "a", first: true));
        f.ContainerUpdates.Add(U(20, "a", sp: MyPawn, s: true, a: 1));
        f.ContainerUpdates.Add(U(300, "a", s: false, a: 0));          // refill (bAlreadySearched back to false)
        f.ContainerUpdates.Add(U(400, "a", s: true, a: 1));           // SearchingPawn unchanged = still me
        Assert.Equal(2, Run(f).ChestsOpened);
    }

    [Fact]
    public void Refill_seen_through_a_snapshot_starts_a_new_cycle()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "a", first: true));
        f.ContainerUpdates.Add(U(20, "a", sp: MatePawn, s: true, a: 1));
        f.ContainerUpdates.Add(U(480, "a", sp: MatePawn, first: true)); // re-sent: not searched any more
        f.ContainerUpdates.Add(U(520, "a", sp: MyPawn, s: true, a: 1));
        var m = Run(f);
        Assert.Equal(1, m.ChestsOpened);
        Assert.Equal(1, m.Events.Count(e => e.By == Attribution.Teammate));
    }

    [Fact]
    public void Spawned_chest_reentering_view_with_a_new_id_is_not_double_counted()
    {
        var f = Facts();
        const string buried = "B_Athena_BuriedChest_Chest_C";
        f.ContainerUpdates.Add(U(10, "dyn#100", buried, first: true, dynamic: true, x: 5000, y: 7000));
        f.ContainerUpdates.Add(U(20, "dyn#100", buried, sp: MyPawn, s: true, a: 1, dynamic: true, x: 5000, y: 7000));
        f.ContainerUpdates.Add(U(400, "dyn#900", buried, sp: MyPawn, s: true, a: 1, first: true, dynamic: true, x: 5040, y: 6980));
        var m = Run(f);
        Assert.Equal(1, m.ChestsOpened);
        Assert.Equal(1, m.MyCount(ContainerKind.BuriedChest));
    }

    [Fact]
    public void Two_spawned_chests_far_apart_are_two_chests()
    {
        var f = Facts();
        const string relic = "B_TieredChest_Relics_C";
        f.ContainerUpdates.Add(U(10, "dyn#1", relic, sp: MyPawn, s: true, a: 1, first: true, dynamic: true, x: 0, y: 0));
        f.ContainerUpdates.Add(U(20, "dyn#2", relic, sp: MyPawn, s: true, a: 1, first: true, dynamic: true, x: 9000, y: 0));
        Assert.Equal(2, Run(f).MyCount(ContainerKind.ExtractionRelicChest));
    }

    [Fact]
    public void Gum_gum_building_smash_counts_each_chest_and_labels_it()
    {
        var f = Facts();
        f.PawnWeapons.Add(new PawnWeaponChange(95, MyPawn, 500));
        f.WeaponActorItem[500] = "WID_TackyGoofiness";
        f.PawnWeapons.Add(new PawnWeaponChange(150, MyPawn, 600));
        f.WeaponActorItem[600] = "WID_Shotgun_Pump_FirePetal_Athena_UC";

        // One building with three chests smashed within a second (pattern observed in real replays).
        foreach (var (id, t) in new[] { ("c1", 100.0f), ("c2", 100.3f), ("c3", 100.9f) })
        {
            f.ContainerUpdates.Add(U(10, id, first: true));
            f.ContainerUpdates.Add(U(t, id, sp: MyPawn, d: true, a: 1));
        }
        // A chest broken long after switching to a shotgun is destroyed, but not by the fruit.
        f.ContainerUpdates.Add(U(10, "c4", first: true));
        f.ContainerUpdates.Add(U(200, "c4", sp: MyPawn, d: true, a: 1));

        var m = Run(f);
        Assert.Equal(4, m.ChestsDestroyed);
        Assert.Equal(3, m.GumGumChestsDestroyed);
    }

    [Fact]
    public void Unattributed_destruction_while_holding_the_fruit_is_reported_but_not_credited()
    {
        var f = Facts();
        f.PawnWeapons.Add(new PawnWeaponChange(95, MyPawn, 500));
        f.WeaponActorItem[500] = "WID_TackyGoofiness";
        f.ContainerUpdates.Add(U(10, "c1", first: true));
        f.ContainerUpdates.Add(U(100, "c1", d: true));
        var m = Run(f);
        Assert.Equal(0, m.ChestsCredited);
        Assert.Equal(1, m.UnattributedDestroyedDuringGumGum);
    }

    [Fact]
    public void Ammo_food_and_loot_cannons_are_tracked_but_not_chests()
    {
        var f = Facts();
        f.ContainerUpdates.Add(U(10, "ammo", "Tiered_Ammo_Athena_C", sp: MyPawn, d: true, a: 1, first: true));
        f.ContainerUpdates.Add(U(10, "food", "FoodBox_Produce_Athena_C", sp: MyPawn, d: true, a: 1, first: true));
        f.ContainerUpdates.Add(U(10, "cannon", "BGA_LootCannon_StandardChest_Pirate_C", sp: MyPawn, s: true, a: 1, first: true));
        f.ContainerUpdates.Add(U(10, "mound", "Prop_Athena_BuriedChest_Sand_C", sp: MyPawn, d: true, a: 1, first: true));
        var m = Run(f);
        Assert.Equal(0, m.ChestsCredited);
        Assert.Equal(1, m.MyCount(ContainerKind.AmmoBox));
        Assert.Equal(1, m.MyCount(ContainerKind.FoodBox));
        Assert.Equal(1, m.MyCount(ContainerKind.LootCannon));
        Assert.DoesNotContain(m.Events, e => e.ClassName.StartsWith("Prop_"));
    }

    [Theory]
    [InlineData("Tiered_Chest_Athena_C", ContainerKind.NormalChest)]
    [InlineData("AlwaysSpawn_NormalChest_C", ContainerKind.NormalChest)]
    [InlineData("Tiered_Chest_5_Child_C", ContainerKind.NormalChest)]
    [InlineData("AlwaysSpawn_RareChest_C", ContainerKind.RareChest)]
    [InlineData("B_Athena_BuriedChest_Chest_C", ContainerKind.BuriedChest)]
    [InlineData("B_Athena_BuriedChest_RelicFinder_C", ContainerKind.BuriedChest)]
    [InlineData("B_TieredChest_Relics_C", ContainerKind.ExtractionRelicChest)]
    [InlineData("B_Delilah_TieredChest_Relics_Variant2_C", ContainerKind.ExtractionRelicChest)]
    [InlineData("BGA_ExtractionPointGameplay_Chest_C", ContainerKind.ExtractionRelicChest)]
    [InlineData("B_SpritePosters_Booty_Chest_C", ContainerKind.SpritePosterChest)]
    [InlineData("Tiered_Ammo_Athena_C", ContainerKind.AmmoBox)]
    [InlineData("FoodBox_Produce_Athena_C", ContainerKind.FoodBox)]
    [InlineData("BGA_LootCannonDuck_Child_C", ContainerKind.LootCannon)]
    [InlineData("BP_Container_WoodenCrates_D_C", ContainerKind.OtherContainer)]
    [InlineData("Prop_Athena_BuriedChest_Sand_C", ContainerKind.Ignored)]
    [InlineData("Some_New_Chest_Blueprint_C", ContainerKind.OtherChest)]
    public void Container_classes_are_categorised(string cls, ContainerKind expected) =>
        Assert.Equal(expected, ContainerKinds.Classify(cls));

    [Fact]
    public void Pickaxe_only_match_is_detected_from_equipped_items()
    {
        var afk = Facts();
        afk.PawnWeapons.Add(new PawnWeaponChange(1, MyPawn, 700));
        afk.PawnWeapons.Add(new PawnWeaponChange(400, MyPawn, 701));
        afk.WeaponActorItem[700] = "WID_Harvest_Pickaxe_FNCS";
        afk.WeaponActorItem[701] = "WID_Harvest_Pickaxe_WinterCamo_Athena";
        afk.PawnWeapons.Add(new PawnWeaponChange(50, MatePawn, 800)); // teammates' items don't matter
        afk.WeaponActorItem[800] = "WID_Shotgun_Pump_FirePetal_Athena_UC";
        Assert.True(Run(afk).OnlyPickaxe);

        var played = Facts();
        played.PawnWeapons.Add(new PawnWeaponChange(1, MyPawn, 700));
        played.PawnWeapons.Add(new PawnWeaponChange(90, MyPawn, 702));
        played.WeaponActorItem[700] = "WID_Harvest_Pickaxe_FNCS";
        played.WeaponActorItem[702] = "WID_Paprika_ShieldSmall";
        Assert.False(Run(played).OnlyPickaxe);

        // No item information at all is not evidence of anything.
        Assert.False(Run(Facts()).OnlyPickaxe);

        // An unresolved item is not assumed to be a pickaxe.
        var unknown = Facts();
        unknown.PawnWeapons.Add(new PawnWeaponChange(1, MyPawn, 999));
        Assert.False(Run(unknown).OnlyPickaxe);
    }

    [Fact]
    public void Match_without_an_identified_recorder_is_unclassified()
    {
        var f = Facts();
        f.RecorderPlayerState = null;
        Assert.Equal(MatchEligibility.Unclassified, Run(f).Eligibility);
    }

    [Fact]
    public void Ranked_match_is_analysed_but_excluded()
    {
        var m = Run(Facts("Playlist_Habanero_Squad"));
        Assert.Equal(ReplayStatus.Analyzed, m.Status);
        Assert.Equal(MatchEligibility.Excluded, m.Eligibility);
    }

    [Fact]
    public void Unfinished_and_unreadable_replays_are_flagged()
    {
        var live = Facts();
        live.Completed = false;
        live.IsLive = true;
        live.Error = "Replay is marked encrypted but not yet marked as completed!";
        Assert.Equal(ReplayStatus.Incomplete, Run(live).Status);

        var broken = Facts();
        broken.Completed = false;
        broken.Error = "Invalid replay file";
        Assert.Equal(ReplayStatus.Unreadable, Run(broken).Status);
    }

    [Fact]
    public void Match_time_comes_from_the_replay_and_is_converted_with_the_pc_time_zone()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");
        var m = new MatchAnalyzer(new AnalyzerOptions { TimeZone = tz }).Analyze(Facts());
        Assert.Equal(new DateTime(2026, 11, 9, 20, 0, 0), m.MatchStartLocal);
        Assert.Equal(new DateTime(2026, 11, 9, 10, 0, 0), m.MatchStartUtc);
        Assert.Equal(900, m.DurationSeconds);
    }
}
