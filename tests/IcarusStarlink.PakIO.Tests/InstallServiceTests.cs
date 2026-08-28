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
        // Same deterministic sibling path InstallAsync itself now derives from stagedPakPath —
        // no longer a separate parameter, so this is just where the fixture file needs to sit.
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
            _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory));
    }

    [Fact]
    public async Task InstallAsync_NoExistingTarget_CopiesPakAndManifestWithNoBackup()
    {
        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.Null(result.BackupPakPath);
        Assert.Equal(TargetPakPath, result.InstalledPakPath);
        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(TargetPakPath));
        Assert.True(File.Exists(TargetManifestPath));
    }

    [Fact]
    public async Task InstallAsync_NoManifestAlongsideThePak_StillInstallsThePak()
    {
        File.Delete(_stagedManifestPath);

        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.True(File.Exists(result.InstalledPakPath));
        Assert.False(File.Exists(TargetManifestPath));
    }

    [Fact]
    public async Task InstallAsync_ManifestPresentButNotPassedIn_IsStillCopied()
    {
        // Regression test for the bug this signature change fixes: the manifest path used to be a
        // caller-supplied, in-memory-cached parameter (MergeInstallViewModel's _lastManifestPath)
        // that went stale after an app restart. It's now always derived from stagedPakPath itself,
        // so a manifest sitting right next to the staged pak is found and copied even though
        // nothing in this test ever told InstallAsync where it was.
        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.True(File.Exists(TargetManifestPath));
        Assert.Equal(await File.ReadAllTextAsync(_stagedManifestPath), await File.ReadAllTextAsync(TargetManifestPath));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task InstallAsync_ExistingTarget_BacksUpOldPakBeforeOverwriting()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPakPath)!);
        await File.WriteAllTextAsync(TargetPakPath, "old pak bytes");

        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

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
            await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
            // Backup filenames are timestamped to the second — without this, two installs in the
            // same second would collide on the same backup filename and silently overwrite each
            // other instead of producing six distinct backups to prune from.
            await Task.Delay(1100);
        }

        var backups = Directory.GetFiles(_backupDirectory, "ISL-Merged_P_*.pak");
        Assert.Equal(5, backups.Length);
    }

    [Fact]
    public async Task InstallAsync_UnrelatedFileAlreadyInModsFolder_IsBackedUpThenRemoved()
    {
        // Real bug this fixes: Content\Paks\mods is meant to hold exactly one active merged pak,
        // but InstallAsync only ever touched its own two specifically-named files — a classic IMM
        // merged pak (or any other stray leftover) sitting there under a different name was left
        // in place, loading side by side with the newly-installed one in-game.
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        var stalePakPath = Path.Combine(modsDirectory, "IMM_Merged_Mod_P.pak");
        await File.WriteAllTextAsync(stalePakPath, "classic IMM's own merged pak bytes");

        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.False(File.Exists(stalePakPath));
        var backedUp = Assert.Single(Directory.GetFiles(_backupDirectory, "IMM_Merged_Mod_P_*.pak"));
        Assert.Equal("classic IMM's own merged pak bytes", await File.ReadAllTextAsync(backedUp));
    }

    [Fact]
    public async Task InstallAsync_UnrelatedFileAlreadyInModsFolder_DoesNotTouchThisAppsOwnFiles()
    {
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        await File.WriteAllTextAsync(Path.Combine(modsDirectory, "SomeOtherMod_P.pak"), "unrelated");

        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(result.InstalledPakPath));
        Assert.True(File.Exists(TargetManifestPath));
    }

    [Fact]
    public async Task InstallAsync_TargetModsDirectoryDoesNotExist_IsCreated()
    {
        Assert.False(Directory.Exists(Path.Combine(_fakeContentPath, "Paks", "mods")));

        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.True(File.Exists(TargetPakPath));
    }

    [Fact]
    public async Task RemoveInstalledPakAsync_NothingInstalled_ReturnsFalseAndDoesNothing()
    {
        var removed = await _service.RemoveInstalledPakAsync("ISL-Merged_P.pak", _fakeContentPath, _backupDirectory);

        Assert.False(removed);
        Assert.False(Directory.Exists(_backupDirectory));
    }

    [Fact]
    public async Task RemoveInstalledPakAsync_AfterInstall_DeletesThePakAndManifestAfterBackingThemUp()
    {
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        var removed = await _service.RemoveInstalledPakAsync("ISL-Merged_P.pak", _fakeContentPath, _backupDirectory);

        Assert.True(removed);
        Assert.False(File.Exists(TargetPakPath));
        Assert.False(File.Exists(TargetManifestPath));
        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(Directory.GetFiles(_backupDirectory, "ISL-Merged_P_*.pak").Single()));
    }

    [Fact]
    public async Task RemoveInstalledPakAsync_OnlyDerivesTheFileNameFromStagedPakFileName_DoesNotRequireItToExistOnDisk()
    {
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        File.Delete(_stagedPakPath);

        var removed = await _service.RemoveInstalledPakAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.True(removed);
        Assert.False(File.Exists(TargetPakPath));
    }

    [Fact]
    public async Task GetInstalledStateAsync_NothingInstalledYet_ReturnsEmptyList()
    {
        var state = await _service.GetInstalledStateAsync(_fakeContentPath);

        Assert.Empty(state.ModNames);
    }

    [Fact]
    public async Task GetInstalledStateAsync_AfterInstall_ReadsBackModNames()
    {
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        var state = await _service.GetInstalledStateAsync(_fakeContentPath);

        Assert.Equal(["Mod A"], state.ModNames);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
