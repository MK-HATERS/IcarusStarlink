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
    public void DeriveFolderName_SingleWrappingFolder_ReturnsThatFolderName()
    {
        var zip = MakeZip("download.zip", ("NearbyCrafting/Scripts/main.lua", "-- lua"), ("NearbyCrafting/enabled.txt", "on"));

        Assert.Equal("NearbyCrafting", Ue4ssModArchive.DeriveFolderName(zip));
    }

    [Fact]
    public void DeriveFolderName_LooseFilesWithNoWrapper_FallsBackToZipFileName()
    {
        var zip = MakeZip("MyMod.zip", ("Scripts/main.lua", "-- lua"), ("enabled.txt", "on"));

        Assert.Equal("MyMod", Ue4ssModArchive.DeriveFolderName(zip));
    }

    [Fact]
    public void DeriveFolderName_MultipleTopLevelFolders_FallsBackToZipFileName()
    {
        var zip = MakeZip("Bundle.zip", ("ModA/main.lua", "a"), ("ModB/main.lua", "b"));

        Assert.Equal("Bundle", Ue4ssModArchive.DeriveFolderName(zip));
    }

    [Fact]
    public void Extract_SingleWrappingFolder_StripsItAndUsesDestinationFolderName()
    {
        var zip = MakeZip("download.zip", ("NearbyCrafting/Scripts/main.lua", "-- lua"), ("NearbyCrafting/enabled.txt", "on"));
        var dest = Path.Combine(_dir, "staged", "NearbyCrafting_2"); // deliberately different name than the zip's own folder

        Ue4ssModArchive.Extract(zip, dest);

        Assert.Equal("-- lua", File.ReadAllText(Path.Combine(dest, "Scripts", "main.lua")));
        Assert.Equal("on", File.ReadAllText(Path.Combine(dest, "enabled.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "NearbyCrafting")));
    }

    [Fact]
    public void Extract_LooseFilesWithNoWrapper_LandsFilesDirectlyUnderDestination()
    {
        var zip = MakeZip("MyMod.zip", ("Scripts/main.lua", "-- lua"), ("enabled.txt", "on"));
        var dest = Path.Combine(_dir, "staged", "MyMod");

        Ue4ssModArchive.Extract(zip, dest);

        Assert.Equal("-- lua", File.ReadAllText(Path.Combine(dest, "Scripts", "main.lua")));
        Assert.Equal("on", File.ReadAllText(Path.Combine(dest, "enabled.txt")));
    }

    [Fact]
    public void Extract_ZipSlipEntry_ThrowsFormatException()
    {
        var zip = MakeZip("evil.zip", ("MyMod/../../../evil.dll", "malicious"));
        var dest = Path.Combine(_dir, "staged", "MyMod");

        Assert.Throws<FormatException>(() => Ue4ssModArchive.Extract(zip, dest));
    }

    [Fact]
    public void Extract_EntryOverTheSizeLimit_ThrowsInsteadOfFullyDecompressingIt()
    {
        var path = Path.Combine(_dir, "huge.zip");
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("MyMod/huge.bin", CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            entryStream.Write(new byte[Container.ExmodSizeLimits.MaxAssetEntryBytes + 1]);
        }

        Assert.Throws<FormatException>(() => Ue4ssModArchive.Extract(path, Path.Combine(_dir, "staged", "MyMod")));
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

    [Fact]
    public void Open_LooseFilesWithNoWrapper_DerivedFolderNameFallsBackToZipFileName()
    {
        // A file sitting directly at the zip root (enabled.txt, one path segment) is what makes
        // this "loose" — a single-entry zip whose one entry happens to sit under a folder would
        // trivially "wrap", which is a different, already-covered case.
        var zip = MakeZip("MyMod.zip", ("Scripts/main.lua", "-- lua"), ("enabled.txt", "on"));

        using var handle = Ue4ssModArchive.Open(zip);

        Assert.Equal("MyMod", handle.DerivedFolderName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
