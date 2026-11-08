using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// A mount's RecorderBlob written back after each kind of edit (talents added, ranked and taken
/// out, genes, lineage, look): it must read back through MountBlob.TryRead (which refuses
/// anything that doesn't write back byte for byte), hold what was set, and come back to the
/// original bytes when everything is set back. A wrong size anywhere can make the game drop the mount.
/// </summary>
public sealed class MountBlobTests
{
    private static byte[] FullBlob(params (string Row, int Rank)[] talents) => new MountBlobFixture.BlobBuilder()
        .AddStr("MountName", "Buddy")
        .AddInt("Experience", 1200)
        .AddStructArray("Talents", "MountTalentSave", "TalentRowName", "TalentRank", talents)
        .AddStructArray("Genetics", "MountGeneticValue", "GeneticValueName", "Value", ("Vitality", 3), ("Endurance", 5))
        .AddName("Lineage", "Wild")
        .AddInt("Variation", 1)
        .Build();

    private static MountBlob Read(byte[] bytes)
    {
        var blob = MountBlob.TryRead(bytes, out var why);
        Assert.True(blob is not null, $"The blob was refused: {why}");
        return blob!;
    }

    [Fact]
    public void Fixture_ReadsAndWritesBackExactly()
    {
        var bytes = FullBlob(("Wolf_Bite", 2));

        var blob = Read(bytes);

        Assert.Equal("Buddy", blob.Name);
        Assert.Equal(1200, blob.Experience);
        Assert.Equal([new MountTalentRank("Wolf_Bite", 2)], blob.Talents);
        Assert.Equal([new MountGene("Vitality", 3), new MountGene("Endurance", 5)], blob.Genetics);
        Assert.Equal("Wild", blob.Lineage);
        Assert.Equal(1, blob.Variation);
        Assert.Equal(bytes, blob.Write());
    }

    [Fact]
    public void AddingATalent_TheMountDoesntHave_WritesTwoEntries_AndTakingItOutGivesTheOriginalBack()
    {
        var bytes = FullBlob(("Wolf_Bite", 2));
        var blob = Read(bytes);

        Assert.True(blob.SetTalentRank("Wolf_Howl", 3));
        var written = Read(blob.Write());

        Assert.Equal([new MountTalentRank("Wolf_Bite", 2), new MountTalentRank("Wolf_Howl", 3)], written.Talents);
        Assert.Equal(blob.Genetics, written.Genetics);
        Assert.Equal("Wild", written.Lineage);

        Assert.True(blob.SetTalentRank("Wolf_Howl", 0));
        Assert.Equal(bytes, blob.Write());
    }

    [Fact]
    public void ARankChange_IsWritten_AndRankZeroTakesTheTalentOut()
    {
        var bytes = FullBlob(("Wolf_Bite", 2));
        var blob = Read(bytes);

        Assert.True(blob.SetTalentRank("Wolf_Bite", 4));
        Assert.Equal([new MountTalentRank("Wolf_Bite", 4)], Read(blob.Write()).Talents);

        Assert.True(blob.SetTalentRank("Wolf_Bite", 0));
        var emptied = Read(blob.Write());
        Assert.Empty(emptied.Talents);
        Assert.Equal(2, emptied.Genetics.Count);
        Assert.Equal(1, emptied.Variation);

        // Back in as a copy of the original entry: the original bytes again.
        Assert.True(blob.SetTalentRank("Wolf_Bite", 2));
        Assert.Equal(bytes, blob.Write());
    }

    [Fact]
    public void AGene_IsWritten_AndSettingItBackGivesTheOriginal()
    {
        var bytes = FullBlob(("Wolf_Bite", 2));
        var blob = Read(bytes);

        blob.SetGene("Endurance", 10);
        Assert.Equal([new MountGene("Vitality", 3), new MountGene("Endurance", 10)], Read(blob.Write()).Genetics);

        blob.SetGene("Endurance", 5);
        Assert.Equal(bytes, blob.Write());
    }

    [Fact]
    public void LineageAndLook_AreWritten_AndSettingThemBackGivesTheOriginal()
    {
        var bytes = FullBlob(("Wolf_Bite", 2));
        var blob = Read(bytes);

        // A longer name changes the property's size: the size written must follow.
        blob.Lineage = "Alpha_Lineage";
        blob.Variation = 3;
        var written = Read(blob.Write());
        Assert.Equal("Alpha_Lineage", written.Lineage);
        Assert.Equal(3, written.Variation);
        Assert.Equal("Buddy", written.Name);
        Assert.Equal([new MountTalentRank("Wolf_Bite", 2)], written.Talents);

        blob.Lineage = "Wild";
        blob.Variation = 1;
        Assert.Equal(bytes, blob.Write());
    }

    /// <summary>A talent is added as a copy of an existing entry: with none to copy, nothing is added.</summary>
    [Fact]
    public void AMountWithNoTalentEntries_CantBeGivenOne()
    {
        var bytes = FullBlob();
        var blob = Read(bytes);
        Assert.Empty(blob.Talents);

        Assert.False(blob.SetTalentRank("Wolf_Bite", 1));
        Assert.Equal(bytes, blob.Write());
    }
}
