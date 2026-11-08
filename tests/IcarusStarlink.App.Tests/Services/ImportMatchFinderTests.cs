using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Tests.Services;

public sealed class ImportMatchFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public ImportMatchFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static LibraryEntry Entry(string folder, string fileName, bool opaque = false, params string[] paks) => new()
    {
        FolderName = folder, Name = folder, Author = "A", Version = "1", Description = "", FileName = fileName, IsOpaquePak = opaque, PakFileNames = paks,
    };

    [Fact]
    public void ReadFolder_AnExmodRenamedOnDisk_MatchesByTheFileNameItDeclares()
    {
        File.WriteAllText(Path.Combine(_root, "MyMod (1).EXMOD"), """{ "name": "My Mod", "fileName": "MyMod", "Rows": [] }""");

        var matches = ImportMatchFinder.Find(ImportMatchFinder.ReadFolder(_root, "x"), [Entry("MyMod", "MyMod")], []);

        Assert.Equal("MyMod", Assert.Single(matches.Library).FolderName);
        Assert.True(matches.IsSingle);
    }

    /// <summary>A pack of variants (the user picks one at import) can't say which installed one it replaces.</summary>
    [Fact]
    public void ReadFolder_SeveralExmods_MatchesNothing()
    {
        File.WriteAllText(Path.Combine(_root, "Stacks_2x.EXMOD"), "{}");
        File.WriteAllText(Path.Combine(_root, "Stacks_5x.EXMOD"), "{}");

        var matches = ImportMatchFinder.Find(ImportMatchFinder.ReadFolder(_root, "x"), [Entry("Stacks_5x", "Stacks_5x")], []);

        Assert.True(matches.IsEmpty);
    }

    [Fact]
    public void Find_PakBundle_MatchesOnlyTheSameSetOfPaks()
    {
        File.WriteAllBytes(Path.Combine(_root, "Icons_P.pak"), TestPaks.Bytes(1));
        File.WriteAllBytes(Path.Combine(_root, "Models_P.pak"), TestPaks.Bytes(2));
        var incoming = ImportMatchFinder.ReadFolder(_root, "x");

        var matches = ImportMatchFinder.Find(incoming,
            [Entry("Bundle", "Bundle", true, "Icons_P.pak", "Models_P.pak"), Entry("IconsOnly", "IconsOnly", true, "Icons_P.pak")], []);

        Assert.Equal("Bundle", Assert.Single(matches.Library).FolderName);
    }
}
