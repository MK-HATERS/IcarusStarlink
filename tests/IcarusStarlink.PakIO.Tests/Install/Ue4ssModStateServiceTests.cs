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

    /// <summary>A mod in the game as UE4SS mods ship (and as every user mod in a real game had them): with its enabled.txt, unless told otherwise.</summary>
    private void CreateGameMod(string name, string content = "-- lua", bool enabledFile = true)
    {
        var folder = Path.Combine(_gameModsFolder, name, "Scripts");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "main.lua"), content);
        if (enabledFile)
        {
            File.WriteAllText(Path.Combine(_gameModsFolder, name, "enabled.txt"), "");
        }
    }

    private void WriteModsTxt(string text) => File.WriteAllText(Path.Combine(_gameModsFolder, "mods.txt"), text);

    private bool HasEnabledFile(string name) => File.Exists(Path.Combine(_gameModsFolder, name, "enabled.txt"));

    // ---- What UE4SS starts (RE-UE4SS UE4SSProgram::start_mods): a "1" in mods.txt, or enabled.txt ----

    [Fact]
    public void GetAll_OnlyWhatUe4ssStartsIsOn()
    {
        CreateGameMod("ByFile");
        CreateGameMod("ByList", enabledFile: false);
        CreateGameMod("OffInList", enabledFile: false);
        CreateGameMod("Nothing", enabledFile: false);
        CreateGameMod("OffInListButFile");
        WriteModsTxt("ByList : 1\r\nOffInList : 0\r\nOffInListButFile : 0\r\n\r\n; Built-in keybinds, do not move up!\r\nKeybinds : 1\r\n");

        var result = _service.GetAll(_gameModsFolder).ToDictionary(m => m.Name);

        Assert.True(result["ByFile"].IsEnabled);
        Assert.True(result["ByList"].IsEnabled);
        // enabled.txt starts a mod even where mods.txt says 0 (UE4SS's second pass).
        Assert.True(result["OffInListButFile"].IsEnabled);
        Assert.False(result["OffInList"].IsEnabled);
        Assert.True(result["OffInList"].IsSwitchedOffInGame);
        Assert.Contains("mods.txt", result["OffInList"].WhyNotStarted);
        Assert.False(result["Nothing"].IsEnabled);
        Assert.True(result["Nothing"].IsSwitchedOffInGame);
        Assert.Contains("no enabled.txt", result["Nothing"].WhyNotStarted);
    }

    /// <summary>UE4SS's shared\ library folder holds no Scripts\main.lua or dlls\main.dll: not a mod, so never "off".</summary>
    [Fact]
    public void GetAll_AFolderThatIsntAMod_IsntOff()
    {
        Directory.CreateDirectory(Path.Combine(_gameModsFolder, "shared", "Types"));

        var shared = _service.GetAll(_gameModsFolder).Single(m => m.Name == "shared");

        Assert.False(shared.IsSwitchedOffInGame);
        Assert.True(shared.IsInGame);
    }

    /// <summary>Turning on a folder that's in the game but off: its enabled.txt, nothing copied or moved.</summary>
    [Fact]
    public void Apply_TurnOnAModOffInUe4ss_WritesItsEnabledFile()
    {
        CreateGameMod("SplitScreenMod", "-- split", enabledFile: false);
        WriteModsTxt("SplitScreenMod : 0\r\n");

        var failures = _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["SplitScreenMod"] = true }, _backupDirectory);

        Assert.Empty(failures);
        Assert.True(HasEnabledFile("SplitScreenMod"));
        Assert.Equal("-- split", File.ReadAllText(Path.Combine(_gameModsFolder, "SplitScreenMod", "Scripts", "main.lua")));
        Assert.True(_service.GetAll(_gameModsFolder).Single(m => m.Name == "SplitScreenMod").IsEnabled);
    }

    /// <summary>A staged mod without an enabled.txt would land in the game doing nothing: it gets one.</summary>
    [Fact]
    public void Apply_EnableAStagedModWithoutEnabledFile_GivesItOne()
    {
        CreateStagedMod("NoFileMod");

        _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["NoFileMod"] = true }, _backupDirectory);

        Assert.True(HasEnabledFile("NoFileMod"));
        Assert.True(_service.GetAll(_gameModsFolder).Single(m => m.Name == "NoFileMod").IsEnabled);
    }

    [Fact]
    public void Apply_EnableAStagedModThatModsTxtStarts_AddsNoEnabledFile()
    {
        CreateStagedMod("ListedMod");
        Directory.CreateDirectory(_gameModsFolder);
        WriteModsTxt("ListedMod : 1\r\n");

        _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["ListedMod"] = true }, _backupDirectory);

        Assert.False(HasEnabledFile("ListedMod"));
        Assert.True(_service.GetAll(_gameModsFolder).Single(m => m.Name == "ListedMod").IsEnabled);
    }

    /// <summary>Taking out a mod that's in the game but off still takes its folder out (a replace or a delete asks for that).</summary>
    [Fact]
    public void Apply_TurnOffAModOffInUe4ss_StillMovesItOut()
    {
        CreateGameMod("Dormant", enabledFile: false);

        _service.Apply(_gameModsFolder, new Dictionary<string, bool> { ["Dormant"] = false }, _backupDirectory);

        Assert.False(Directory.Exists(Path.Combine(_gameModsFolder, "Dormant")));
        Assert.True(Directory.Exists(Path.Combine(_stagedDirectory, "Dormant")));
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
