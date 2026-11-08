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
