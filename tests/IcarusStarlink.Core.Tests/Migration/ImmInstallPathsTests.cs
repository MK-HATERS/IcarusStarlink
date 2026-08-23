using IcarusStarlink.Core.Migration;

namespace IcarusStarlink.Core.Tests.Migration;

public sealed class ImmInstallPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"ImmPaths_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateImmInstall(string name = "Icarus Software")
    {
        var installRoot = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(installRoot, ImmInstallPaths.ExtractedModsFolderName));
        File.WriteAllText(Path.Combine(installRoot, ImmExtractedMods.FileName), """{"mods":[]}""");
        return installRoot;
    }

    [Fact]
    public void FindInstallRoot_ListSittingInTheInstallFolder_IsFound()
    {
        // The real, common case: LastMergedMods.txt lives right next to Extracted_Mods.
        var installRoot = CreateImmInstall();
        var listPath = Path.Combine(installRoot, "LastMergedMods.txt");
        File.WriteAllText(listPath, "Includes the following mods:");

        Assert.Equal(installRoot, ImmInstallPaths.FindInstallRoot(listPath));
    }

    [Fact]
    public void FindInstallRoot_ListOneFolderDeeper_IsStillFound()
    {
        var installRoot = CreateImmInstall();
        var nested = Path.Combine(installRoot, "Backups");
        Directory.CreateDirectory(nested);
        var listPath = Path.Combine(nested, "LastMergedMods.txt");
        File.WriteAllText(listPath, "Includes the following mods:");

        Assert.Equal(installRoot, ImmInstallPaths.FindInstallRoot(listPath));
    }

    [Fact]
    public void FindInstallRoot_ListFromTheGameFolder_ReturnsNullRatherThanGuessing()
    {
        // IMM_Merged_Mod.txt sits in the game's Paks\mods folder, nowhere near an IMM install —
        // the caller has to ask the user where the mods actually are.
        var gameMods = Path.Combine(_root, "Icarus", "Content", "Paks", "mods");
        Directory.CreateDirectory(gameMods);
        var listPath = Path.Combine(gameMods, "IMM_Merged_Mod.txt");
        File.WriteAllText(listPath, "Includes the following mods:");

        Assert.Null(ImmInstallPaths.FindInstallRoot(listPath));
    }

    [Fact]
    public void LooksLikeInstallRoot_NeedsBothMarkers()
    {
        var onlyFolder = Path.Combine(_root, "HalfInstall");
        Directory.CreateDirectory(Path.Combine(onlyFolder, ImmInstallPaths.ExtractedModsFolderName));

        // An Extracted_Mods folder alone isn't proof — this app's own data folder has one too.
        Assert.False(ImmInstallPaths.LooksLikeInstallRoot(onlyFolder));
        Assert.True(ImmInstallPaths.LooksLikeInstallRoot(CreateImmInstall("Real")));
    }
}
