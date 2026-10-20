using System.IO.Compression;
using IcarusStarlink.PakIO.Ue4ss;

namespace IcarusStarlink.PakIO.Tests;

public class Ue4ssModArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public Ue4ssModArchiveTests() => Directory.CreateDirectory(_dir);

    private string MakeZip(string fileName, params (string EntryPath, string Content)[] entries)
    {
        var path = Path.Combine(_dir, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void DerivedFolderName_SingleWrappingFolder_ReturnsThatFolderName()
    {
        var zip = MakeZip("download.zip", ("NearbyCrafting/Scripts/main.lua", "-- lua"), ("NearbyCrafting/enabled.txt", "on"));

        using var handle = Ue4ssModArchive.Open(zip);
        Assert.Equal("NearbyCrafting", handle.DerivedFolderName);
    }

    [Fact]
    public void DerivedFolderName_LooseFilesWithNoWrapper_FallsBackToZipFileName()
    {
        var zip = MakeZip("MyMod.zip", ("Scripts/main.lua", "-- lua"), ("enabled.txt", "on"));

        using var handle = Ue4ssModArchive.Open(zip);
        Assert.Equal("MyMod", handle.DerivedFolderName);
    }

    [Fact]
    public void DerivedFolderName_MultipleTopLevelFolders_FallsBackToZipFileName()
    {
        var zip = MakeZip("Bundle.zip", ("ModA/main.lua", "a"), ("ModB/main.lua", "b"));

        using var handle = Ue4ssModArchive.Open(zip);
        Assert.Equal("Bundle", handle.DerivedFolderName);
    }

    [Fact]
    public void ExtractTo_SingleWrappingFolder_StripsItAndUsesDestinationFolderName()
    {
        var zip = MakeZip("download.zip", ("NearbyCrafting/Scripts/main.lua", "-- lua"), ("NearbyCrafting/enabled.txt", "on"));
        var dest = Path.Combine(_dir, "staged", "NearbyCrafting_2"); // deliberately different name than the zip's own folder

        using (var handle = Ue4ssModArchive.Open(zip))
        {
            handle.ExtractTo(dest);
        }

        Assert.Equal("-- lua", File.ReadAllText(Path.Combine(dest, "Scripts", "main.lua")));
        Assert.Equal("on", File.ReadAllText(Path.Combine(dest, "enabled.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "NearbyCrafting")));
    }

    [Fact]
    public void ExtractTo_LooseFilesWithNoWrapper_LandsFilesDirectlyUnderDestination()
    {
        var zip = MakeZip("MyMod.zip", ("Scripts/main.lua", "-- lua"), ("enabled.txt", "on"));
        var dest = Path.Combine(_dir, "staged", "MyMod");

        using (var handle = Ue4ssModArchive.Open(zip))
        {
            handle.ExtractTo(dest);
        }

        Assert.Equal("-- lua", File.ReadAllText(Path.Combine(dest, "Scripts", "main.lua")));
        Assert.Equal("on", File.ReadAllText(Path.Combine(dest, "enabled.txt")));
    }

    [Fact]
    public void ExtractTo_ZipSlipEntry_ThrowsFormatException()
    {
        var zip = MakeZip("evil.zip", ("MyMod/../../../evil.dll", "malicious"));
        var dest = Path.Combine(_dir, "staged", "MyMod");

        // Open+ExtractTo both inside the lambda — the containment check may fire at either stage,
        // and the guarantee under test is only that it fires before anything lands on disk.
        Assert.Throws<FormatException>(() =>
        {
            using var handle = Ue4ssModArchive.Open(zip);
            handle.ExtractTo(dest);
        });
    }

    [Fact]
    public void ExtractTo_EntryOverTheSizeLimit_ThrowsInsteadOfFullyDecompressingIt()
    {
        var path = Path.Combine(_dir, "huge.zip");
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("MyMod/huge.bin", CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            entryStream.Write(new byte[Container.ExmodSizeLimits.MaxAssetEntryBytes + 1]);
        }

        Assert.Throws<FormatException>(() =>
        {
            using var handle = Ue4ssModArchive.Open(path);
            handle.ExtractTo(Path.Combine(_dir, "staged", "MyMod"));
        });
    }

    [Fact]
    public void Open_DerivedFolderNameThenExtractTo_BothReflectTheSameSingleScan()
    {
        // The real point of Open() — a caller (Ue4ssModRepository.Import) needs both the derived
        // name AND extraction, off one opened/scanned archive rather than two independent ones.
        var zip = MakeZip("download.zip", ("NearbyCrafting/Scripts/main.lua", "-- lua"), ("NearbyCrafting/enabled.txt", "on"));
        var dest = Path.Combine(_dir, "staged", "NearbyCrafting");

        using var handle = Ue4ssModArchive.Open(zip);
        Assert.Equal("NearbyCrafting", handle.DerivedFolderName);
        handle.ExtractTo(dest);

        Assert.Equal("-- lua", File.ReadAllText(Path.Combine(dest, "Scripts", "main.lua")));
        Assert.Equal("on", File.ReadAllText(Path.Combine(dest, "enabled.txt")));
    }

    /// <summary>
    /// A zip holding only Scripts/ (or dlls/) is already the mod: that folder is UE4SS's own layout,
    /// not a wrapper. Stripped, the mod was named "Scripts" with main.lua at its root, so UE4SS
    /// never started it. (The same check the folder import already makes.)
    /// </summary>
    [Theory]
    [InlineData("Scripts/main.lua")]
    [InlineData("dlls/main.dll")]
    public void Open_OnlyTheUe4ssLayoutFolder_IsNotAWrapper(string entryPath)
    {
        var zip = MakeZip("MyMod.zip", (entryPath, "x"));
        var dest = Path.Combine(_dir, "staged", "MyMod");

        using var handle = Ue4ssModArchive.Open(zip);
        Assert.Equal("MyMod", handle.DerivedFolderName);
        handle.ExtractTo(dest);

        Assert.True(File.Exists(Path.Combine(dest, entryPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
