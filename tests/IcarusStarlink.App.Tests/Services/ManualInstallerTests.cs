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
        WriteBytes("Paks/A_P.pak", TestPaks.Bytes("pak"));
        WriteBytes("Paks/B_P.pak", TestPaks.Bytes("pak"));
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

    private void WriteBytes(string relative, byte[] content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
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

    /// <summary>A UE4SS mod's typed name becomes its folder name: "UE4SS Mods\MiniMapa" used to stage a "UE4SS Mods" folder with the mod inside, and "Name: 2" failed with a Windows path error.</summary>
    [Theory]
    [InlineData("UE4SS Mods/MiniMapa")]
    [InlineData("UE4SS Mods\\MiniMapa")]
    [InlineData("Name: 2")]
    [InlineData("MiniMapa.")]
    [InlineData("CON")]
    [InlineData("..")]
    public void Problem_Ue4ssNameThatIsntAFolderName_IsRefused(string name)
    {
        var line = new ManualInstallItem(ManualInstallKind.Ue4ssMod, Path.Combine(_root, "UE4SS Mods", "MiniMapa"), "UE4SS Mods/MiniMapa", "MiniMapa") { Name = name };

        Assert.Contains("folder name", ManualInstaller.Problem(line));
    }

    [Fact]
    public void Problem_Ue4ssPlainName_IsFine()
    {
        var line = new ManualInstallItem(ManualInstallKind.Ue4ssMod, Path.Combine(_root, "UE4SS Mods", "MiniMapa"), "UE4SS Mods/MiniMapa", "MiniMapa") { Name = " MiniMapa " };

        Assert.Null(ManualInstaller.Problem(line));
    }

    /// <summary>A UE4SS name with a slash staged a "UE4SS Mods" folder (never started by UE4SS); it is refused before anything is staged.</summary>
    [Fact]
    public async Task Install_Ue4ssNameWithASlash_StagesNothing()
    {
        var store = Path.Combine(_root, "_store");
        using var library = new IcarusStarlink.Storage.Library.FolderLibraryRepository(
            Path.Combine(store, "Extracted_Mods"), Path.Combine(store, "Library_Meta"), Path.Combine(store, "Backups"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IcarusStarlink.Storage.Library.FolderLibraryRepository>.Instance);
        var ue4ss = new IcarusStarlink.Storage.Ue4ss.Ue4ssModRepository(Path.Combine(store, "Staged_UE4SS"));
        IReadOnlyList<ManualInstallItem> plan =
        [
            new(ManualInstallKind.Ue4ssMod, Path.Combine(_root, "UE4SS Mods", "MiniMapa"), "UE4SS Mods/MiniMapa", "MiniMapa") { Name = "UE4SS Mods/MiniMapa" },
        ];

        await Assert.ThrowsAsync<FormatException>(() => ManualInstaller.InstallAsync(
            plan, library, ue4ss, new FailingPakImporter(), Path.Combine(_root, "Data"), null, null, null, null, null));

        Assert.Empty(ue4ss.GetAll());
        Assert.False(Directory.Exists(Path.Combine(store, "Staged_UE4SS", "UE4SS Mods")));
    }

    /// <summary>Adding the same piece twice installed it twice (a second "_2" copy): the second add is refused.</summary>
    [Fact]
    public void DuplicateProblem_RefusesThePathAlreadyInThePlan()
    {
        var plan = new List<ManualInstallItem> { new(ManualInstallKind.PakMod, Path.Combine(_root, "Paks"), "Paks", "Paks") };

        Assert.Contains("already in the list", ManualInstaller.DuplicateProblem(plan, new ManualInstallItem(ManualInstallKind.PakMod, Path.Combine(_root, "Paks"), "Paks", "Other")));
        Assert.Null(ManualInstaller.DuplicateProblem(plan, new ManualInstallItem(ManualInstallKind.Ue4ssMod, Path.Combine(_root, "UE4SS Mods", "MiniMapa"), "x", "MiniMapa")));
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

    private sealed class CountingPakImporter : IcarusStarlink.PakIO.Import.IPrebuiltPakImporter
    {
        public int Calls { get; private set; }

        public Task<IcarusStarlink.Core.Library.LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath, string? source = null, int? nexusModId = null,
            string? catalogEntryId = null, string? name = null, string? author = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new IcarusStarlink.Core.Library.LibraryEntry
            {
                FolderName = "Web", Name = "Web", Author = "Unknown", Version = "1.0", Description = "", FileName = "Web", IsOpaquePak = true,
            });
        }
    }

    /// <summary>A single pak line that is a web page went straight to the importer; it is refused with the plain reason before anything is installed.</summary>
    [Fact]
    public async Task Install_APakThatIsntARealPak_IsRefusedBeforeAnythingGoesIn()
    {
        Write("Web_P.pak", "<html>Not Found</html>");
        var importer = new CountingPakImporter();
        IReadOnlyList<ManualInstallItem> plan = [new(ManualInstallKind.PakMod, Path.Combine(_root, "Web_P.pak"), "Web_P.pak", "Web")];

        var ex = await Assert.ThrowsAsync<FormatException>(() => ManualInstaller.InstallAsync(
            plan, null!, null!, importer, Path.Combine(_root, "Data"), null, null, null, null, null));

        Assert.Contains("isn't a working .pak file", ex.Message);
        Assert.Equal(0, importer.Calls);
    }

    /// <summary>A reinstall checks the plan before the installed copy is taken out: a pak line in a folder holding a web page is refused.</summary>
    [Fact]
    public void CheckPlan_APakThatIsntARealPak_IsRefused()
    {
        WriteBytes("Mixed/Good_P.pak", TestPaks.Bytes("good"));
        Write("Mixed/Web_P.pak", "<html>Not Found</html>");

        var ex = Assert.Throws<FormatException>(() => ManualInstaller.CheckPlan(
            [new ManualInstallItem(ManualInstallKind.PakMod, Path.Combine(_root, "Mixed"), "Mixed", "Mixed")]));

        Assert.Contains("'Web_P.pak' isn't a working .pak file", ex.Message);
        ManualInstaller.CheckPlan([new ManualInstallItem(ManualInstallKind.PakMod, Path.Combine(_root, "Paks"), "Paks", "Paks")]);
    }

    /// <summary>A folder line holding two variants with one pak name failed half-way with "file already exists".</summary>
    [Fact]
    public void Problem_AFolderWithTwoPaksOfOneName_SaysToAddTheFolderYouWant()
    {
        WriteBytes("Variants/Option A/X_P.pak", TestPaks.Bytes("a"));
        WriteBytes("Variants/Option B/X_P.pak", TestPaks.Bytes("b"));

        var problem = ManualInstaller.Problem(new ManualInstallItem(ManualInstallKind.PakMod, Path.Combine(_root, "Variants"), "Variants", "Variants"));

        Assert.NotNull(problem);
        Assert.Contains("more than one pak named 'X_P.pak'", problem);
        Assert.Contains("Option A", problem);
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
