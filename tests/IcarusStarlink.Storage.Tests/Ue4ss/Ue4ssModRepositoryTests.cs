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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
