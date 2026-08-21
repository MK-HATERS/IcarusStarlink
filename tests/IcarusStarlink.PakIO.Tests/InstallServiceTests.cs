using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.PakIO.Tests;

public class InstallServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _stagedPakPath;
    private readonly string _stagedManifestPath;
    private readonly string _fakeContentPath;
    private readonly string _backupDirectory;
    private readonly InstallService _service = new();

    public InstallServiceTests()
    {
        _stagedPakPath = Path.Combine(_tempDir, "Staged_Build", "ISL-Merged_P.pak");
        _stagedManifestPath = Path.Combine(_tempDir, "Staged_Build", "ISL-Merged.txt");
        _fakeContentPath = Path.Combine(_tempDir, "FakeIcarusContent");
        _backupDirectory = Path.Combine(_tempDir, "Backups");

        Directory.CreateDirectory(Path.GetDirectoryName(_stagedPakPath)!);
        File.WriteAllText(_stagedPakPath, "staged pak v1 bytes");
        File.WriteAllText(_stagedManifestPath, "Includes the following mods:\nMod A");
    }

    private string TargetPakPath => Path.Combine(_fakeContentPath, "Paks", "mods", "ISL-Merged_P.pak");
    private string TargetManifestPath => Path.Combine(_fakeContentPath, "Paks", "mods", "ISL-Merged.txt");

    [Fact]
    public async Task InstallAsync_MissingStagedPak_ThrowsFileNotFoundException()
    {
        File.Delete(_stagedPakPath);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [], _fakeContentPath, _backupDirectory));
    }

    [Fact]
    public async Task InstallAsync_NoExistingTarget_CopiesPakAndManifestWithNoBackup()
    {
        var result = await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [], _fakeContentPath, _backupDirectory);

        Assert.Null(result.BackupPakPath);
        Assert.Equal(TargetPakPath, result.InstalledPakPath);
        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(TargetPakPath));
        Assert.True(File.Exists(TargetManifestPath));
    }

    [Fact]
    public async Task InstallAsync_ManifestPathNull_StillInstallsThePak()
    {
        var result = await _service.InstallAsync(_stagedPakPath, stagedManifestPath: null, [], _fakeContentPath, _backupDirectory);

        Assert.True(File.Exists(result.InstalledPakPath));
        Assert.False(File.Exists(TargetManifestPath));
    }

    [Fact]
    public async Task InstallAsync_ExistingTarget_BacksUpOldPakBeforeOverwriting()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPakPath)!);
        await File.WriteAllTextAsync(TargetPakPath, "old pak bytes");

        var result = await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [], _fakeContentPath, _backupDirectory);

        Assert.NotNull(result.BackupPakPath);
        Assert.Equal("old pak bytes", await File.ReadAllTextAsync(result.BackupPakPath!));
        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(TargetPakPath));
    }

    [Fact]
    public async Task InstallAsync_SixInstallsOverExistingTarget_KeepsOnlyFiveMostRecentBackups()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPakPath)!);
        await File.WriteAllTextAsync(TargetPakPath, "initial pak bytes");

        for (var i = 1; i <= 6; i++)
        {
            await File.WriteAllTextAsync(_stagedPakPath, $"staged pak v{i} bytes");
            await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [], _fakeContentPath, _backupDirectory);
            // Backup filenames are timestamped to the second — without this, two installs in the
            // same second would collide on the same backup filename and silently overwrite each
            // other instead of producing six distinct backups to prune from.
            await Task.Delay(1100);
        }

        var backups = Directory.GetFiles(_backupDirectory, "ISL-Merged_P_*.pak");
        Assert.Equal(5, backups.Length);
    }

    [Fact]
    public async Task InstallAsync_TargetModsDirectoryDoesNotExist_IsCreated()
    {
        Assert.False(Directory.Exists(Path.Combine(_fakeContentPath, "Paks", "mods")));

        await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [], _fakeContentPath, _backupDirectory);

        Assert.True(File.Exists(TargetPakPath));
    }

    // Ue4ssGamePaths.ResolveModsFolder treats icarusContentPath's own PARENT as the game root
    // (matching the real relationship: ...\Icarus\Icarus\Content and ...\Icarus\Icarus\Binaries\...
    // are siblings) — mirror that exact derivation here rather than assuming _fakeContentPath
    // itself is the root.
    private string GameModsFolder => Path.Combine(Path.GetDirectoryName(_fakeContentPath)!, "Binaries", "Win64", "ue4ss", "Mods");

    private string StageUe4ssMod(string modName, string fileContent = "-- lua")
    {
        var folder = Path.Combine(_tempDir, "Staged_UE4SS", modName);
        Directory.CreateDirectory(Path.Combine(folder, "Scripts"));
        File.WriteAllText(Path.Combine(folder, "Scripts", "main.lua"), fileContent);
        return folder;
    }

    [Fact]
    public async Task InstallAsync_StagedUe4ssMod_CopiedIntoGameModsFolder()
    {
        var staged = StageUe4ssMod("NearbyCrafting");

        var result = await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [staged], _fakeContentPath, _backupDirectory);

        Assert.Equal(["NearbyCrafting"], result.InstalledUe4ssModNames);
        Assert.Equal("-- lua", await File.ReadAllTextAsync(Path.Combine(GameModsFolder, "NearbyCrafting", "Scripts", "main.lua")));
    }

    [Fact]
    public async Task InstallAsync_Ue4ssModAlreadyPresentInGame_BacksItUpBeforeOverwriting()
    {
        Directory.CreateDirectory(Path.Combine(GameModsFolder, "NearbyCrafting"));
        await File.WriteAllTextAsync(Path.Combine(GameModsFolder, "NearbyCrafting", "old.txt"), "old version");
        var staged = StageUe4ssMod("NearbyCrafting", "new version");

        await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [staged], _fakeContentPath, _backupDirectory);

        var backupFolder = Directory.GetDirectories(Path.Combine(_backupDirectory, "UE4SS"), "NearbyCrafting_*").Single();
        Assert.True(File.Exists(Path.Combine(backupFolder, "old.txt")));
        Assert.False(File.Exists(Path.Combine(GameModsFolder, "NearbyCrafting", "old.txt")));
    }

    [Fact]
    public async Task InstallAsync_ThenInstallAgainWithoutThatMod_RemovesThePreviouslyInstalledOne()
    {
        var staged = StageUe4ssMod("NearbyCrafting");
        await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [staged], _fakeContentPath, _backupDirectory);
        Assert.True(Directory.Exists(Path.Combine(GameModsFolder, "NearbyCrafting")));

        // Second Install with an empty UE4SS set — the profile no longer has that mod attached.
        var result = await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [], _fakeContentPath, _backupDirectory);

        Assert.Empty(result.InstalledUe4ssModNames);
        Assert.False(Directory.Exists(Path.Combine(GameModsFolder, "NearbyCrafting")));
    }

    [Fact]
    public async Task InstallAsync_DoesNotTouchAUe4ssModFolderItDidNotInstall()
    {
        // A built-in UE4SS framework mod, or one the user installed by hand — never tracked by
        // this app's own manifest, so a sync must never remove it.
        Directory.CreateDirectory(Path.Combine(GameModsFolder, "ConsoleEnablerMod"));

        await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [], _fakeContentPath, _backupDirectory);

        Assert.True(Directory.Exists(Path.Combine(GameModsFolder, "ConsoleEnablerMod")));
    }

    [Fact]
    public async Task RemoveInstalledUe4ssModsAsync_RemovesOnlyWhatThisAppInstalled()
    {
        Directory.CreateDirectory(Path.Combine(GameModsFolder, "ConsoleEnablerMod")); // not ours
        var staged = StageUe4ssMod("NearbyCrafting");
        await _service.InstallAsync(_stagedPakPath, _stagedManifestPath, [staged], _fakeContentPath, _backupDirectory);

        await _service.RemoveInstalledUe4ssModsAsync(_fakeContentPath);

        Assert.False(Directory.Exists(Path.Combine(GameModsFolder, "NearbyCrafting")));
        Assert.True(Directory.Exists(Path.Combine(GameModsFolder, "ConsoleEnablerMod")));
    }

    [Fact]
    public async Task RemoveInstalledUe4ssModsAsync_NothingEverInstalled_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() => _service.RemoveInstalledUe4ssModsAsync(_fakeContentPath));
        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
