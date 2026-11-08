using IcarusStarlink.Core.IO;
using IcarusStarlink.Storage.Library;
using IcarusStarlink.Storage.Ue4ss;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests;

/// <summary>
/// A mod folder name must name one folder under the root. Regression: an empty name used to
/// resolve to the root itself and ".." to the root's parent, so Delete("") removed every mod in
/// Extracted_Mods, and a rooted or nested name made a new mod land outside the root.
/// </summary>
public class ModFoldersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public static TheoryData<string> NamesThatAreNotOneFolder => ["", " ", ".", "..", @"..\Other", @"Sub\Mod", "Sub/Mod", @"C:\Windows", "readme.md:evil", "CON"];

    private string Root()
    {
        var root = Path.Combine(_dir, "Root");
        Directory.CreateDirectory(root);
        return root;
    }

    [Theory]
    [MemberData(nameof(NamesThatAreNotOneFolder))]
    public void Resolve_NameThatIsNotOneFolder_SaysNotFoundInsteadOfReturningAnotherFolder(string name)
    {
        var root = Root();
        Directory.CreateDirectory(Path.Combine(root, "Sub", "Mod"));

        var ex = Assert.Throws<DirectoryNotFoundException>(() => ModFolders.Resolve(root, name, "No mod by that name."));

        Assert.Equal("No mod by that name.", ex.Message);
    }

    [Theory]
    [MemberData(nameof(NamesThatAreNotOneFolder))]
    public void MakeUnique_NameThatIsNotOneFolder_IsRefused(string name)
    {
        var root = Root();

        Assert.Throws<ArgumentException>(() => ModFolders.MakeUnique(root, name));
    }

    [Fact]
    public void MakeUnique_PlainName_StillAddsASuffixWhenTaken()
    {
        var root = Root();
        Directory.CreateDirectory(Path.Combine(root, "Mod"));

        Assert.Equal("Mod_2", ModFolders.MakeUnique(root, "Mod"));
    }

    [Fact]
    public void LibraryDelete_EmptyName_LeavesEveryOtherModInPlace()
    {
        var extractedMods = Path.Combine(_dir, "Extracted_Mods");
        var otherMod = Path.Combine(extractedMods, "SomeoneElsesMod");
        Directory.CreateDirectory(otherMod);
        File.WriteAllText(Path.Combine(otherMod, "SomeoneElsesMod.pak"), "pak");
        using var repo = new FolderLibraryRepository(extractedMods, Path.Combine(_dir, "meta"), Path.Combine(_dir, "backups"), NullLogger<FolderLibraryRepository>.Instance);

        Assert.Throws<DirectoryNotFoundException>(() => repo.Delete(""));

        Assert.True(File.Exists(Path.Combine(otherMod, "SomeoneElsesMod.pak")));
    }

    [Fact]
    public void StagedUe4ssGetFolderPath_DotDot_IsNotTheParentFolder()
    {
        var repo = new Ue4ssModRepository(Path.Combine(_dir, "Staged_UE4SS"));

        Assert.Throws<DirectoryNotFoundException>(() => repo.GetFolderPath(".."));
    }

    public void Dispose()
    {
        Directories.TryDelete(_dir);
        GC.SuppressFinalize(this);
    }
}
