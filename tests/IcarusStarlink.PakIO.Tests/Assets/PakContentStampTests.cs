using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// A game file's stamp follows its contents, not where the game packed it: a game update that
/// repacks the paks moves every file, and the maps were all drawn again for it.
/// </summary>
public sealed class PakContentStampTests
{
    private static PakEntryShape Entry(string pak = "pakchunk0-WindowsNoEditor.pak", long offset = 1000, long size = 5000, long compressed = 3000,
        string method = "Oodle", params (long Start, long End)[] blocks) =>
        new(pak, offset, size, compressed, method, blocks.Length > 0 ? blocks : [(offset + 60, offset + 1560), (offset + 1560, offset + 3060)]);

    [Fact]
    public void TheSameFile_InAnotherPak_OrAtAnotherPlace_HasTheSameStamp()
    {
        var before = PakContentStamp.Of(".uasset", Entry());

        Assert.Equal(before, PakContentStamp.Of(".uasset", Entry(pak: "pakchunk3-WindowsNoEditor.pak")));
        Assert.Equal(before, PakContentStamp.Of(".uasset", Entry(offset: 987_654)));
    }

    [Fact]
    public void AFileWhoseSizeChanged_HasAnotherStamp()
    {
        var before = PakContentStamp.Of(".uasset", Entry());

        Assert.NotEqual(before, PakContentStamp.Of(".uasset", Entry(size: 5001)));
        Assert.NotEqual(before, PakContentStamp.Of(".uasset", Entry(compressed: 2999)));
        Assert.NotEqual(before, PakContentStamp.Of(".uasset", Entry(method: "Zlib")));
    }

    /// <summary>The same size packed into blocks of other lengths: the bytes changed (a texture keeps its size when its picture changes).</summary>
    [Fact]
    public void AFileWhoseCompressionBlocksChanged_HasAnotherStamp() =>
        Assert.NotEqual(
            PakContentStamp.Of(".uexp", Entry(blocks: [(60, 1560), (1560, 3060)])),
            PakContentStamp.Of(".uexp", Entry(blocks: [(60, 1570), (1570, 3060)])));

    private sealed class Game(Func<string, string?> stamp) : IBaseGameContentProvider
    {
        public T? TryLoadExport<T>(string assetPath) where T : class => null;

        public TResult? TryLoadExportAndProject<T, TResult>(string assetPath, Func<T, TResult> project)
            where T : class where TResult : class => null;

        public string? FileStamp(string assetPath) => stamp(assetPath);
    }

    private static WorldMapInfo Olympus => new("Terrain_016", "Olympus", false, [new WorldTile(0, 0, "PROC/x0_y0.uasset")], [], [],
        LevelFiles: ["Maps/Terrain_016_OLY/Terrain_016.umap"]);

    /// <summary>The scheme is named in the stamp, so maps drawn under an older one can be told apart (and kept).</summary>
    [Fact]
    public void AMapsStamp_NamesItsScheme_AndFollowsItsFiles()
    {
        var stamp = new WorldMapRenderer(new Game(path => path + ":1")).Stamp(Olympus);

        Assert.StartsWith("c2:", stamp);
        Assert.Equal(stamp, new WorldMapRenderer(new Game(path => path + ":1")).Stamp(Olympus));
        Assert.NotEqual(stamp, new WorldMapRenderer(new Game(path => path.EndsWith(".umap", StringComparison.Ordinal) ? path + ":2" : path + ":1")).Stamp(Olympus));
    }
}
