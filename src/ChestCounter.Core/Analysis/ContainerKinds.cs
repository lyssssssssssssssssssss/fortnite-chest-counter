namespace ChestCounter.Core.Analysis;

/// <summary>What a searchable container is. Only the *Chest* kinds count towards the chest total.</summary>
public enum ContainerKind
{
    NormalChest,
    RareChest,
    BuriedChest,
    ExtractionRelicChest,
    SpritePosterChest,
    OtherChest,

    AmmoBox,
    FoodBox,
    LootCannon,
    OtherContainer,

    /// <summary>Not a container in its own right (e.g. the sand mound over a buried chest).</summary>
    Ignored,
}

public static class ContainerKinds
{
    public static bool IsChest(this ContainerKind kind) => kind is
        ContainerKind.NormalChest or ContainerKind.RareChest or ContainerKind.BuriedChest
        or ContainerKind.ExtractionRelicChest or ContainerKind.SpritePosterChest or ContainerKind.OtherChest;

    public static string Label(this ContainerKind kind) => kind switch
    {
        ContainerKind.NormalChest => "Chest",
        ContainerKind.RareChest => "Rare chest",
        ContainerKind.BuriedChest => "Buried chest",
        ContainerKind.ExtractionRelicChest => "Extraction / relic chest",
        ContainerKind.SpritePosterChest => "Sprite poster chest",
        ContainerKind.OtherChest => "Other chest",
        ContainerKind.AmmoBox => "Ammo box",
        ContainerKind.FoodBox => "Food box",
        ContainerKind.LootCannon => "Loot cannon",
        ContainerKind.OtherContainer => "Other container",
        _ => "Ignored",
    };

    /// <summary>
    /// Classifies a replicated class name (e.g. "Tiered_Chest_Athena_C"). The names below were observed in
    /// real Fortnite 43.00 replays; unknown names containing "Chest" still count as "Other chest" so a
    /// new chest blueprint is never silently dropped.
    /// </summary>
    public static ContainerKind Classify(string className)
    {
        var n = className.EndsWith("_C", StringComparison.Ordinal) ? className[..^2] : className;
        bool Has(string s) => n.Contains(s, StringComparison.OrdinalIgnoreCase);

        // Not containers on their own: the dig-site mound that sits on top of a buried chest.
        if (n.StartsWith("Prop_", StringComparison.OrdinalIgnoreCase))
            return ContainerKind.Ignored;

        // Loot cannons (BGA_LootCannon_StandardChest_Pirate, BGA_LootCannonDuck_Child) are not chests even
        // though one of them has "Chest" in its name.
        if (Has("LootCannon"))
            return ContainerKind.LootCannon;

        if (Has("Ammo"))
            return ContainerKind.AmmoBox;

        if (Has("FoodBox") || Has("Food_Box") || Has("Cooler"))
            return ContainerKind.FoodBox;

        if (Has("Chest"))
        {
            if (Has("Buried")) return ContainerKind.BuriedChest;                       // B_Athena_BuriedChest_Chest, B_Athena_BuriedChest_RelicFinder
            if (Has("SpritePoster") || Has("Booty")) return ContainerKind.SpritePosterChest; // B_SpritePosters_Booty_Chest
            if (Has("Relic") || Has("Extraction")) return ContainerKind.ExtractionRelicChest; // B_TieredChest_Relics, BGA_ExtractionPointGameplay_Chest
            if (Has("Rare") || Has("Legendary")) return ContainerKind.RareChest;      // AlwaysSpawn_RareChest
            if (Has("Tiered_Chest") || Has("NormalChest") || Has("StandardChest")) return ContainerKind.NormalChest; // Tiered_Chest_Athena, AlwaysSpawn_NormalChest, Tiered_Chest_5_Child
            return ContainerKind.OtherChest;
        }

        return ContainerKind.OtherContainer; // BP_Container_* crates/barrels and anything else searchable
    }
}
