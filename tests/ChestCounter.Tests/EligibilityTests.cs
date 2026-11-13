using ChestCounter.Core.Analysis;

namespace ChestCounter.Tests;

/// <summary>Game-mode filtering. Playlist names marked "observed" were seen in real Fortnite 43.00 replays.</summary>
public class EligibilityTests
{
    private readonly EligibilityClassifier _c = EligibilityClassifier.Create(null, null);

    [Theory]
    [InlineData("Playlist_NoBuildBR_Squad", "Zero Build Squads")]       // observed
    [InlineData("Playlist_DefaultSquad", "Battle Royale Squads")]      // observed
    [InlineData("Playlist_DefaultDuo", "Battle Royale Duos")]          // observed
    [InlineData("Playlist_DefaultSolo", "Battle Royale Solo")]
    [InlineData("Playlist_Trios", "Battle Royale Trios")]
    [InlineData("Playlist_NoBuildBR_Solo", "Zero Build Solo")]
    [InlineData("Playlist_NoBuildBR_Duo", "Zero Build Duos")]
    [InlineData("Playlist_NoBuildBR_Trio", "Zero Build Trios")]
    public void Public_battle_royale_is_eligible(string playlist, string label)
    {
        var r = _c.Classify([playlist]);
        Assert.Equal(MatchEligibility.Eligible, r.Eligibility);
        Assert.Equal(label, r.ModeLabel);
    }

    [Fact]
    public void Full_asset_paths_are_understood()
    {
        var r = _c.Classify(["/BRPlaylists/Athena/Playlists/NoBuildBR/Playlist_NoBuildBR_Squad.Playlist_NoBuildBR_Squad"]);
        Assert.Equal(MatchEligibility.Eligible, r.Eligibility);
    }

    [Theory]
    [InlineData("Playlist_Habanero_PiperBoot_Duos", "Ranked")]                              // observed (ranked)
    [InlineData("Playlist_Habanero_Solo", "Ranked")]
    [InlineData("Playlist_Habanero_NoBuildBR_Squad", "Ranked")]                             // ranked ZB must not slip through
    [InlineData("Playlist_ShowdownTournament_RE_SourSpawnSolo_NPM", "Tournament / competitive")] // observed (tournament)
    [InlineData("Playlist_ShowdownAlt_Solo", "Tournament / competitive")]
    [InlineData("Playlist_Blastberry_Squads", "Reload")]
    [InlineData("Playlist_Punchberry_Duos", "Reload")]
    [InlineData("Playlist_Sunflower_Squads", "Reload")]
    [InlineData("Playlist_VK_Play", "Other mode (Creative / LEGO / OG / Festival / LTM)")] // observed (Creative/UEFN)
    [InlineData("Playlist_Juno_Survival", "Other mode (Creative / LEGO / OG / Festival / LTM)")]
    [InlineData("Playlist_Figment_Squads", "Other mode (Creative / LEGO / OG / Festival / LTM)")]
    public void Ranked_tournaments_reload_and_creative_are_excluded(string playlist, string label)
    {
        var r = _c.Classify([playlist]);
        Assert.Equal(MatchEligibility.Excluded, r.Eligibility);
        Assert.Equal(label, r.ModeLabel);
    }

    [Fact]
    public void Unknown_playlists_are_unclassified_not_counted()
    {
        Assert.Equal(MatchEligibility.Unclassified, _c.Classify(["Playlist_SomethingBrandNew"]).Eligibility);
        Assert.Equal(MatchEligibility.Unclassified, _c.Classify([]).Eligibility);
    }

    [Fact]
    public void Any_ineligible_playlist_in_a_match_excludes_it()
    {
        Assert.NotEqual(MatchEligibility.Eligible, _c.Classify(["Playlist_DefaultSquad", "Playlist_Habanero_Squad"]).Eligibility);
    }

    [Fact]
    public void User_overrides_take_precedence()
    {
        var c = EligibilityClassifier.Create(["Playlist_SomethingBrandNew"], ["Playlist_DefaultDuo"]);
        Assert.Equal(MatchEligibility.Eligible, c.Classify(["Playlist_SomethingBrandNew"]).Eligibility);
        Assert.Equal(MatchEligibility.Excluded, c.Classify(["Playlist_DefaultDuo"]).Eligibility);
        Assert.Equal(MatchEligibility.Eligible, c.Classify(["Playlist_DefaultSquad"]).Eligibility);
    }
}
