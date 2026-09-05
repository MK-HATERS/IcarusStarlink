using IcarusStarlink.PakIO.Install;
using IcarusStarlink.Storage.Ue4ss;

namespace IcarusStarlink.PakIO.Tests.Install;

public class Ue4ssModStateServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _stagedDirectory;
    private readonly string _gameModsFolder;
    private readonly string _backupDirectory;
    private readonly Ue4ssModRepository _stagingRepository;
    private readonly Ue4ssModStateService _service;

    public Ue4ssModStateServiceTests()
    {
        _stagedDirectory = Path.Combine(_tempDir, "Staged_UE4SS");
        _gameModsFolder = Path.Combine(_tempDir, "GameMods");
        _backupDirectory = Path.Combine(_tempDir, "Backups");
        _stagingRepository = new Ue4ssModRepository(_stagedDirectory);
        _service = new Ue4ssModStateService(_stagingRepository);
    }

    private void CreateGameMod(string name, string content = "-- lua")
    {
        var folder = Path.Combine(_gameModsFolder, name, "Scripts");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.lua"), content);
    }

    private void CreateStagedMod(string name, string content = "-- lua")
    {
        var folder = Path.Combine(_stagedDirectory, name, "Scripts");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.lua"), content);
    }

    [Fact]
    public void GetAll_UnionsGameAndStagedByName()
    {
        CreateGameMod("EnabledOne");
        CreateStagedMod("DisabledOne");

        var result = _service.GetAll(_gameModsFolder);

        Assert.Equal(2, result.Count);
        Assert.True(result.Single(m => m.Name == "EnabledOne").IsEnabled);
        Assert.False(result.Single(m => m.Name == "DisabledOne").IsEnabled);
    }

    [Fact]
    public void Apply_EnableAStagedMod_MovesItIntoGameFolderAndOutOfStaging()
    {
        CreateStagedMod("NearbyCrafting", "-- real content");

        _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["NearbyCrafting"] = true }, _backupDirectory);

        Assert.Equal("-- real content", File.ReadAllText(Path.Combine(_gameModsFolder, "NearbyCrafting", "Scripts", "main.lua")));
        Assert.False(Directory.Exists(Path.Combine(_stagedDirectory, "NearbyCrafting")));
    }

    [Fact]
    public void Apply_DisableAnEnabledMod_MovesItIntoStagingAndOutOfGameFolder()
    {
        CreateGameMod("NearbyCrafting", "-- real content");

        _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["NearbyCrafting"] = false }, _backupDirectory);

        Assert.False(Directory.Exists(Path.Combine(_gameModsFolder, "NearbyCrafting")));
        Assert.Equal("-- real content", File.ReadAllText(Path.Combine(_stagedDirectory, "NearbyCrafting", "Scripts", "main.lua")));
    }

    [Fact]
    public void Apply_DisableAnEnabledMod_BacksUpTheGameCopyFirst()
    {
        CreateGameMod("NearbyCrafting", "-- real content");

        _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["NearbyCrafting"] = false }, _backupDirectory);

        var backupFolder = Directory.GetDirectories(Path.Combine(_backupDirectory, "UE4SS"), "NearbyCrafting_*").Single();
        Assert.Equal("-- real content", File.ReadAllText(Path.Combine(backupFolder, "Scripts", "main.lua")));
    }

    [Fact]
    public void Apply_DesiredStateMatchesCurrentState_DoesNothing()
    {
        CreateGameMod("AlreadyEnabled");

        _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["AlreadyEnabled"] = true }, _backupDirectory);

        Assert.True(Directory.Exists(Path.Combine(_gameModsFolder, "AlreadyEnabled")));
        Assert.False(Directory.Exists(Path.Combine(_backupDirectory, "UE4SS")));
    }

    /// <summary>
    /// Regression guard: enabling a staged mod folder with no actual files in it (a stale/broken
    /// staged entry) used to "succeed" — CopyDirectory happily copies zero files, and the real
    /// staged copy (or lack thereof) then got deleted anyway, leaving neither a working staged copy
    /// nor a working enabled one, with no error surfaced at all.
    /// </summary>
    [Fact]
    public void Apply_EnableAModWithNoFilesStaged_ReportsFailureAndLeavesStagingUntouched()
    {
        Directory.CreateDirectory(Path.Combine(_stagedDirectory, "Empty"));

        var failures = _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["Empty"] = true }, _backupDirectory);

        var failure = Assert.Single(failures);
        Assert.Equal("Empty", failure.Name);
        Assert.True(Directory.Exists(Path.Combine(_stagedDirectory, "Empty")));
        Assert.False(Directory.Exists(Path.Combine(_gameModsFolder, "Empty")));
    }

    /// <summary>
    /// Regression guard: one mod's failure used to throw out of Apply entirely, silently skipping
    /// every OTHER mod later in the same dictionary with no indication they were never attempted —
    /// a locked file on mod #1 meant mod #2 (which had nothing wrong with it) never got processed.
    /// </summary>
    [Fact]
    public void Apply_OneModFails_OtherModsInTheSameBatchStillGetApplied()
    {
        Directory.CreateDirectory(Path.Combine(_stagedDirectory, "Broken")); // no files
        CreateStagedMod("Fine", "-- real content");

        var failures = _service.Apply(
            _gameModsFolder,
            new Dictionary<string, bool> { ["Broken"] = true, ["Fine"] = true },
            _backupDirectory);

        Assert.Single(failures, f => f.Name == "Broken");
        Assert.Equal("-- real content", File.ReadAllText(Path.Combine(_gameModsFolder, "Fine", "Scripts", "main.lua")));
        Assert.False(Directory.Exists(Path.Combine(_stagedDirectory, "Fine")));
    }

    [Fact]
    public void Apply_Success_ReturnsNoFailures()
    {
        CreateStagedMod("Fine");

        var failures = _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["Fine"] = true }, _backupDirectory);

        Assert.Empty(failures);
    }

    [Fact]
    public void Apply_NameNotInDesiredDictionary_IsLeftAlone()
    {
        CreateGameMod("Untouched");
        CreateStagedMod("AlsoUntouched");

        _service.Apply(_gameModsFolder, new Dictionary<string, bool>(), _backupDirectory);

        Assert.True(Directory.Exists(Path.Combine(_gameModsFolder, "Untouched")));
        Assert.True(Directory.Exists(Path.Combine(_stagedDirectory, "AlsoUntouched")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
