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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
