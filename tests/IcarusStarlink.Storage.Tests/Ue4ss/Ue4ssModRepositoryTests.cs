using System.IO.Compression;
using IcarusStarlink.Storage.Ue4ss;

namespace IcarusStarlink.Storage.Tests.Ue4ss;

public class Ue4ssModRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _stagedDir;
    private readonly string _gameModsDir;

    public Ue4ssModRepositoryTests()
    {
        _stagedDir = Path.Combine(_dir, "Staged_UE4SS");
        _gameModsDir = Path.Combine(_dir, "GameMods");
    }

    private Ue4ssModRepository CreateRepository() => new(_stagedDir);

    private string MakeModZip(string zipFileName, string modFolderName)
    {
        var zipPath = Path.Combine(_dir, zipFileName);
        Directory.CreateDirectory(_dir);
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry($"{modFolderName}/Scripts/main.lua");
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream);
        writer.Write("-- lua");
        return zipPath;
    }

    [Fact]
    public void GetAll_NoModsStagedYet_IsEmpty()
    {
        Assert.Empty(CreateRepository().GetAll());
    }

    [Fact]
    public void Import_ThenGetAll_ListsTheStagedModByItsFolderName()
    {
        var repo = CreateRepository();
        var zip = MakeModZip("NearbyCrafting.zip", "NearbyCrafting");

        var folderName = repo.Import(zip);

        Assert.Equal("NearbyCrafting", folderName);
        Assert.Equal(["NearbyCrafting"], repo.GetAll());
        Assert.True(File.Exists(Path.Combine(repo.GetFolderPath("NearbyCrafting"), "Scripts", "main.lua")));
    }

    [Fact]
    public void Import_SameModTwice_DisambiguatesRatherThanOverwriting()
    {
        var repo = CreateRepository();
        repo.Import(MakeModZip("first.zip", "NearbyCrafting"));
        var second = repo.Import(MakeModZip("second.zip", "NearbyCrafting"));

        Assert.Equal("NearbyCrafting_2", second);
        Assert.Equal(["NearbyCrafting", "NearbyCrafting_2"], repo.GetAll());
    }

    [Fact]
    public void Delete_RemovesTheStagedModFolder()
    {
        var repo = CreateRepository();
        repo.Import(MakeModZip("mod.zip", "NearbyCrafting"));

        repo.Delete("NearbyCrafting");

        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void GetFolderPath_UnknownMod_ThrowsDirectoryNotFoundException()
    {
        Assert.Throws<DirectoryNotFoundException>(() => CreateRepository().GetFolderPath("NoSuchMod"));
    }

    [Fact]
    public void ListInstalledInGame_GameModsFolderDoesNotExist_IsEmptyRatherThanThrowing()
    {
        Assert.Empty(CreateRepository().ListInstalledInGame(_gameModsDir));
    }

    [Fact]
    public void ListInstalledInGame_ListsRealFolderNamesUnderTheGivenPath()
    {
        Directory.CreateDirectory(Path.Combine(_gameModsDir, "ConsoleEnablerMod"));
        Directory.CreateDirectory(Path.Combine(_gameModsDir, "CenterUICursor"));

        var result = CreateRepository().ListInstalledInGame(_gameModsDir);

        Assert.Equal(["CenterUICursor", "ConsoleEnablerMod"], result);
    }

    [Fact]
    public void AdoptFromGame_RealFolderInGame_CopiesIntoStagingAndLeavesGameFolderUntouched()
    {
        var gameModFolder = Path.Combine(_gameModsDir, "ActorDumperMod", "Scripts");
        Directory.CreateDirectory(gameModFolder);
        File.WriteAllText(Path.Combine(gameModFolder, "main.lua"), "-- framework built-in");

        var repo = CreateRepository();
        var stagedName = repo.AdoptFromGame(_gameModsDir, "ActorDumperMod");

        Assert.Equal("ActorDumperMod", stagedName);
        Assert.Equal(["ActorDumperMod"], repo.GetAll());
        Assert.Equal("-- framework built-in", File.ReadAllText(Path.Combine(repo.GetFolderPath("ActorDumperMod"), "Scripts", "main.lua")));
        // The source is a copy, not a move — the real game folder must still have its own copy.
        Assert.True(File.Exists(Path.Combine(gameModFolder, "main.lua")));
    }

    [Fact]
    public void AdoptFromGame_AlreadyStagedUnderThatName_DisambiguatesRatherThanOverwriting()
    {
        var repo = CreateRepository();
        repo.Import(MakeModZip("first.zip", "ActorDumperMod"));
        Directory.CreateDirectory(Path.Combine(_gameModsDir, "ActorDumperMod"));

        var adopted = repo.AdoptFromGame(_gameModsDir, "ActorDumperMod");

        Assert.Equal("ActorDumperMod_2", adopted);
    }

    [Fact]
    public void AdoptFromGame_NotActuallyInGameFolder_ThrowsDirectoryNotFoundException()
    {
        Directory.CreateDirectory(_gameModsDir);

        Assert.Throws<DirectoryNotFoundException>(() => CreateRepository().AdoptFromGame(_gameModsDir, "NoSuchMod"));
    }

    [Fact]
    public void ImportFromFolder_SingleWrappingSubfolder_StripsItAndNamesTheModAfterIt()
    {
        var extracted = Path.Combine(_dir, "Extracted1");
        var wrapping = Path.Combine(extracted, "NearbyCrafting");
        Directory.CreateDirectory(Path.Combine(wrapping, "Scripts"));
        File.WriteAllText(Path.Combine(wrapping, "Scripts", "main.lua"), "-- lua");

        var repo = CreateRepository();
        var folderName = repo.ImportFromFolder(extracted, "SomeArchiveName");

        Assert.Equal("NearbyCrafting", folderName);
        Assert.True(File.Exists(Path.Combine(repo.GetFolderPath("NearbyCrafting"), "Scripts", "main.lua")));
    }

    [Fact]
    public void ImportFromFolder_LooseFilesAtRoot_UsesFallbackNameSinceThereIsNoWrappingFolder()
    {
        var extracted = Path.Combine(_dir, "Extracted2");
        Directory.CreateDirectory(Path.Combine(extracted, "Scripts"));
        File.WriteAllText(Path.Combine(extracted, "Scripts", "main.lua"), "-- lua");
        File.WriteAllText(Path.Combine(extracted, "readme.txt"), "hi");

        var repo = CreateRepository();
        var folderName = repo.ImportFromFolder(extracted, "Rada-CheatMenu");

        Assert.Equal("Rada-CheatMenu", folderName);
        Assert.True(File.Exists(Path.Combine(repo.GetFolderPath("Rada-CheatMenu"), "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(repo.GetFolderPath("Rada-CheatMenu"), "readme.txt")));
    }

    /// <summary>
    /// Regression guard: staging's own uniqueness check used to only ever look at what's already
    /// staged, never at what's already installed in the game — so a newly-imported mod could land
    /// under the exact same folder name as a completely unrelated, already-enabled game mod.
    /// Ue4ssModStateService.GetAll's own name-keyed union of staged+installed mods would then
    /// silently treat the two as "the same mod", making the just-imported one invisible/inaccessible
    /// in the UI (still on disk, but with no way to select or enable it) until the other one
    /// happened to get disabled.
    /// </summary>
    [Fact]
    public void Import_NameCollidesWithAnAlreadyInstalledGameMod_DisambiguatesAgainstItToo()
    {
        var repo = CreateRepository();
        var zip = MakeModZip("mod.zip", "NearbyCrafting");

        var folderName = repo.Import(zip, namesAlreadyInUse: ["NearbyCrafting"]);

        Assert.Equal("NearbyCrafting_2", folderName);
    }

    [Fact]
    public void ImportFromFolder_NameCollidesWithAnAlreadyInstalledGameMod_DisambiguatesAgainstItToo()
    {
        var extracted = Path.Combine(_dir, "ExtractedCollision");
        var wrapping = Path.Combine(extracted, "NearbyCrafting");
        Directory.CreateDirectory(wrapping);
        File.WriteAllText(Path.Combine(wrapping, "main.lua"), "-- lua");

        var folderName = CreateRepository().ImportFromFolder(extracted, "NearbyCrafting", namesAlreadyInUse: ["NearbyCrafting"]);

        Assert.Equal("NearbyCrafting_2", folderName);
    }

    [Fact]
    public void AdoptFromGame_NameCollidesWithAnAlreadyInstalledGameMod_DisambiguatesAgainstItToo()
    {
        var gameModFolder = Path.Combine(_gameModsDir, "ActorDumperMod");
        Directory.CreateDirectory(gameModFolder);
        File.WriteAllText(Path.Combine(gameModFolder, "main.lua"), "-- content");

        var adopted = CreateRepository().AdoptFromGame(_gameModsDir, "ActorDumperMod", namesAlreadyInUse: ["ActorDumperMod"]);

        Assert.Equal("ActorDumperMod_2", adopted);
    }

    [Fact]
    public void ImportFromFolder_NameCollision_DisambiguatesRatherThanOverwriting()
    {
        var repo = CreateRepository();
        repo.Import(MakeModZip("first.zip", "NearbyCrafting"));

        var extracted = Path.Combine(_dir, "Extracted3");
        var wrapping = Path.Combine(extracted, "NearbyCrafting");
        Directory.CreateDirectory(wrapping);
        File.WriteAllText(Path.Combine(wrapping, "main.lua"), "-- lua v2");

        var folderName = repo.ImportFromFolder(extracted, "NearbyCrafting");

        Assert.Equal("NearbyCrafting_2", folderName);
        Assert.Equal(["NearbyCrafting", "NearbyCrafting_2"], repo.GetAll());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
