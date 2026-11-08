using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>Facts from the game's code that aren't tested in game are kept apart from the tested ones and say so.</summary>
public sealed class KnownGameBehaviourTests
{
    [Fact]
    public void EveryFactFromTheGamesCode_SaysItIsNotTestedInGame()
    {
        Assert.NotEmpty(KnownGameBehaviour.FromGameCode);
        Assert.All(KnownGameBehaviour.FromGameCode, f => Assert.StartsWith("From the game's code, not tested in game:", f.Note, StringComparison.Ordinal));
        Assert.Contains(KnownGameBehaviour.HammerRepairs, KnownGameBehaviour.FromGameCode);
        Assert.Contains(KnownGameBehaviour.SeedNumbers, KnownGameBehaviour.FromGameCode);
    }

    [Fact]
    public void RepairNeeds_HaveAFieldNote_IgnoringCase_AndOtherFieldsNone()
    {
        Assert.Equal(KnownGameBehaviour.HammerRepairs.Note, KnownGameBehaviour.FieldNoteFromGameCode("d_durable", "itemsForRepair"));
        Assert.Contains("hammer", KnownGameBehaviour.FieldNoteFromGameCode("D_Durable", "ItemsForRepair"));
        Assert.Null(KnownGameBehaviour.FieldNoteFromGameCode("D_Durable", "Max_Durability"));
        Assert.Null(KnownGameBehaviour.FieldNoteFromGameCode("D_ItemsStatic", "ItemsForRepair"));
    }

    /// <summary>
    /// Regression: the texts players read in mod checks, the mod editor and the Editor page used to
    /// name who tested a fact and on which day. They keep "tested in game" only.
    /// </summary>
    [Fact]
    public void TestedFacts_PlayersRead_SayTestedInGame_WithoutNamesOrDates()
    {
        string?[] texts =
        [
            .. KnownGameBehaviour.BuiltInTables.Values.Select(t => t.What),
            KnownGameBehaviour.StatWithNoEffect("D_ItemsStatic", "AdditionalStats", "GrantedAuraHealthRegen_?", placedItem: true),
            KnownGameBehaviour.StatWithNoEffect("D_InventoryInfo", "Stats", KnownGameBehaviour.SpoilRateStat, placedItem: false),
        ];

        Assert.All(texts, text =>
        {
            Assert.NotNull(text);
            Assert.DoesNotContain("Hook Scout", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MkHaters", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", text);
        });
        Assert.Contains("(tested in game)", KnownGameBehaviour.BuiltInTables["D_CharacterGrowth"].What);
        Assert.StartsWith("Changes nothing", texts[^1]);
    }

    /// <summary>The tested facts don't carry the "not tested" words, so the two kinds can't be mixed up.</summary>
    [Fact]
    public void TestedFacts_DontSayTheyAreFromTheGamesCode()
    {
        var tested = KnownGameBehaviour.StatWithNoEffect("D_InventoryInfo", "Stats", KnownGameBehaviour.SpoilRateStat, placedItem: false);

        Assert.NotNull(tested);
        Assert.DoesNotContain(KnownGameBehaviour.FromGameCodePrefix, tested);
        Assert.All(KnownGameBehaviour.BuiltInTables.Values, t => Assert.DoesNotContain(KnownGameBehaviour.FromGameCodePrefix, t.What));
    }
}
