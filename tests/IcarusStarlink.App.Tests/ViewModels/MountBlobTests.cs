using System.IO;
using System.Text;
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
    /// <summary>
    /// Builds a blob of tagged properties the way MountBlob reads them (UE4, before the UE5 tag
    /// layout): each tag is name, type, size, array index, the type's own header, then a 0 byte
    /// (no property GUID), then the value. An array of structs holds a count, one inner struct tag
    /// (with the size of all the elements), then each element's properties ending with "None".
    /// </summary>
    private sealed class BlobBuilder
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;

        public BlobBuilder() => _writer = new BinaryWriter(_stream);

        private static byte[] FString(string s) => [.. BitConverter.GetBytes(s.Length + 1), .. Encoding.ASCII.GetBytes(s), 0];

        private static byte[] Tag(string name, string type, int size, byte[] header) =>
            [.. FString(name), .. FString(type), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(0), .. header, 0];

        private static byte[] Str(string name, string value, string type = "StrProperty")
        {
            var bytes = FString(value);
            return [.. Tag(name, type, bytes.Length, []), .. bytes];
        }

        private static byte[] Int(string name, int value) => [.. Tag(name, "IntProperty", 4, []), .. BitConverter.GetBytes(value)];

        public BlobBuilder AddStr(string name, string value)
        {
            _writer.Write(Str(name, value));
            return this;
        }

        public BlobBuilder AddName(string name, string value)
        {
            _writer.Write(Str(name, value, "NameProperty"));
            return this;
        }

        public BlobBuilder AddInt(string name, int value)
        {
            _writer.Write(Int(name, value));
            return this;
        }

        /// <summary>An array of structs, each element one (StrProperty, IntProperty) pair.</summary>
        public BlobBuilder AddStructArray(string name, string structName, string keyName, string valueName, params (string Key, int Value)[] elements)
        {
            var body = elements.SelectMany(e => (byte[])[.. Str(keyName, e.Key), .. Int(valueName, e.Value), .. FString("None")]).ToArray();
            byte[] inner = [.. FString(name), .. FString("StructProperty"), .. BitConverter.GetBytes(body.Length), .. BitConverter.GetBytes(0),
                .. FString(structName), .. new byte[16], 0];
            byte[] value = [.. BitConverter.GetBytes(elements.Length), .. inner, .. body];
            _writer.Write(Tag(name, "ArrayProperty", value.Length, FString("StructProperty")));
            _writer.Write(value);
            return this;
        }

        public byte[] Build()
        {
            _writer.Write(FString("None"));
            _writer.Write(0);
            _writer.Flush();
            return _stream.ToArray();
        }
    }

    private static byte[] FullBlob(params (string Row, int Rank)[] talents) => new BlobBuilder()
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
