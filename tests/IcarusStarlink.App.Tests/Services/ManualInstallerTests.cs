using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Manual install's starting guesses and checks, on the shape of the real "Minimap V1.0.5" download.</summary>
public sealed class ManualInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Manual_" + Guid.NewGuid().ToString("N"));

    public ManualInstallerTests()
    {
        Write("Icarus Mod Manager/Minimap.EXMODZ", "zip");
        Write("Icarus Mod Manager/Minimap_PTBR.EXMODZ", "zip");
        Write("UE4SS Mods/MiniMapa/Scripts/main.lua", "-- lua");
        Write("UE4SS Mods/MiniMapa/icons/ore.png", "png");
        Write("Paks/A_P.pak", "pak");
        Write("Paks/B_P.pak", "pak");
        Write("README_EN.txt", "readme");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Suggest_FindsEachPiece_AndLeavesReadmesOut()
    {
        var guesses = ManualInstaller.Suggest(_root);

        Assert.Contains(guesses, g => g.Kind == ManualInstallKind.Ue4ssMod && g.Name == "MiniMapa");
        Assert.Contains(guesses, g => g.Kind == ManualInstallKind.ExmodMod && g.Name == "Minimap");
        Assert.Contains(guesses, g => g.Kind == ManualInstallKind.ExmodMod && g.Name == "Minimap_PTBR");
        var paks = Assert.Single(guesses, g => g.Kind == ManualInstallKind.PakMod);
        Assert.Equal("Paks", paks.Name); // two paks in one folder: one mod
        Assert.DoesNotContain(guesses, g => g.RelativePath.Contains("README") || g.RelativePath.Contains("icons"));
        Assert.Equal(4, guesses.Count);
    }

    [Fact]
    public void Problem_RefusesAKindThatDoesNotFit()
    {
        var readme = new ManualInstallItem(ManualInstallKind.PakMod, Path.Combine(_root, "README_EN.txt"), "README_EN.txt", "Readme");
        var luaAsExmod = new ManualInstallItem(ManualInstallKind.ExmodMod, Path.Combine(_root, "UE4SS Mods", "MiniMapa"), "UE4SS Mods/MiniMapa", "MiniMapa");
        var archiveAsUe4ss = new ManualInstallItem(ManualInstallKind.Ue4ssMod, Path.Combine(_root, "Icarus Mod Manager", "Minimap.EXMODZ"), "x", "Minimap");
        var unnamed = new ManualInstallItem(ManualInstallKind.Ue4ssMod, Path.Combine(_root, "UE4SS Mods", "MiniMapa"), "x", " ");

        Assert.Contains("isn't a .pak", ManualInstaller.Problem(readme));
        Assert.Contains("no .EXMOD", ManualInstaller.Problem(luaAsExmod));
        Assert.Contains("is a file", ManualInstaller.Problem(archiveAsUe4ss));
        Assert.Contains("give it a name", ManualInstaller.Problem(unnamed));
        Assert.Null(ManualInstaller.Problem(new ManualInstallItem(ManualInstallKind.PakMod, Path.Combine(_root, "Paks"), "Paks", "Paks")));
    }

    private sealed class FailingPakImporter : IcarusStarlink.PakIO.Import.IPrebuiltPakImporter
    {
        public Task<IcarusStarlink.Core.Library.LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath, string? source = null, int? nexusModId = null,
            string? catalogEntryId = null, string? name = null, string? author = null, CancellationToken cancellationToken = default) =>
            throw new FormatException("broken pak");
    }

    /// <summary>A manual install is all or nothing: a later piece failing takes the earlier ones back out, so nothing is left untracked.</summary>
    [Fact]
    public async Task Install_APieceThatFails_TakesBackThePiecesAlreadyIn()
    {
        var store = Path.Combine(_root, "_store");
        using var library = new IcarusStarlink.Storage.Library.FolderLibraryRepository(
            Path.Combine(store, "Extracted_Mods"), Path.Combine(store, "Library_Meta"), Path.Combine(store, "Backups"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IcarusStarlink.Storage.Library.FolderLibraryRepository>.Instance);
        var ue4ss = new IcarusStarlink.Storage.Ue4ss.Ue4ssModRepository(Path.Combine(store, "Staged_UE4SS"));
        IReadOnlyList<ManualInstallItem> plan =
        [
            new(ManualInstallKind.Ue4ssMod, Path.Combine(_root, "UE4SS Mods", "MiniMapa"), "UE4SS Mods/MiniMapa", "MiniMapa"),
            new(ManualInstallKind.PakMod, Path.Combine(_root, "Paks", "A_P.pak"), "Paks/A_P.pak", "A"),
        ];

        var ex = await Assert.ThrowsAsync<FormatException>(() => ManualInstaller.InstallAsync(
            plan, library, ue4ss, new FailingPakImporter(), Path.Combine(_root, "Data"), null, null, null, null, null));

        Assert.Equal("broken pak", ex.Message);
        Assert.Empty(ue4ss.GetAll());
    }

    [Fact]
    public void UnpackForManualInstall_ABrokenArchive_LeavesNoTempFolderBehind()
    {
        var broken = Path.Combine(_root, "broken.zip");
        File.WriteAllText(broken, "not a zip");
        var before = Directory.Exists(Path.Combine(Path.GetTempPath(), "IcarusStarlink"))
            ? Directory.GetDirectories(Path.Combine(Path.GetTempPath(), "IcarusStarlink"), "Manual_*").ToHashSet()
            : [];

        Assert.ThrowsAny<Exception>(() => ManualInstaller.UnpackForManualInstall(broken));

        var after = Directory.Exists(Path.Combine(Path.GetTempPath(), "IcarusStarlink"))
            ? Directory.GetDirectories(Path.Combine(Path.GetTempPath(), "IcarusStarlink"), "Manual_*")
            : [];
        Assert.DoesNotContain(after, d => !before.Contains(d));
    }
}
