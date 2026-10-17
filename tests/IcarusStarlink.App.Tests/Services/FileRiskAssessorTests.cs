using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Over real Library folders: only copies of game files count, and two different copies of one clash.</summary>
public sealed class FileRiskAssessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FileRisk_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    public FileRiskAssessorTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>The game has the player character and one texture; nothing else.</summary>
    private sealed class FakeGame : IBaseGameContentProvider
    {
        private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
        {
            "BP/Player/BP_IcarusPlayerCharacterSurvival.uasset", "Assets/T_Shared_Icon.uasset",
        };

        public bool? HasAsset(string assetPath) => Files.Contains(assetPath);

        public T? TryLoadExport<T>(string assetPath) where T : class => null;

        public TResult? TryLoadExportAndProject<T, TResult>(string assetPath, Func<T, TResult> project) where T : class where TResult : class => null;
    }

    private (string Folder, string Name) Mod(string name, params (string Path, byte Content)[] assets)
    {
        var entry = _library.CreateBlankMod(name, "Me", ModTemplate.Blank);
        var folder = _library.GetFolderPath(entry.FolderName);
        ExmodFolder.Write(folder, new ExmodPackageContents(ExmodFolder.ReadPackageOnly(folder), [.. assets.Select(a => new ExmodAssetEntry(a.Path, [a.Content, 1, 2]))]));
        _library.Refresh();
        return (entry.FolderName, name);
    }

    [Fact]
    public void OnlyCopiesOfGameFilesCount_AndDifferentCopiesOfOneClash()
    {
        var mover = Mod("Mover", ("BP/Player/BP_IcarusPlayerCharacterSurvival.uasset", 1), ("BP/Mover/BP_Own_Cargo_Box.uasset", 1));
        var flyer = Mod("Flyer", ("BP/Player/BP_IcarusPlayerCharacterSurvival.uasset", 2));
        var iconA = Mod("Icons A", ("Assets/T_Shared_Icon.uasset", 7));
        var iconB = Mod("Icons B", ("Assets/T_Shared_Icon.uasset", 7));
        var own = Mod("Only Own Files", ("BP/Own/BP_Something_New.uasset", 3));

        var risks = FileRiskAssessor.Assess(_library, [mover, flyer, iconA, iconB, own], new FakeGame());

        var moverRisk = risks[mover.Folder];
        Assert.Equal(FileRisk.High, moverRisk.Level);
        Assert.Equal(["BP/Player/BP_IcarusPlayerCharacterSurvival"], moverRisk.Files.Select(f => f.Path));
        Assert.Equal(["Flyer"], Assert.Single(moverRisk.Clashes).OtherMods);
        Assert.Equal(["Mover"], Assert.Single(risks[flyer.Folder].Clashes).OtherMods);

        // The same unchanged texture in two mods is duplication, not a clash.
        Assert.Empty(risks[iconA.Folder].Clashes);
        Assert.Equal(FileRisk.Low, risks[iconA.Folder].Level);

        // Files the game doesn't have are the mod's own: no risk at all.
        Assert.False(risks.ContainsKey(own.Folder));
    }
}
