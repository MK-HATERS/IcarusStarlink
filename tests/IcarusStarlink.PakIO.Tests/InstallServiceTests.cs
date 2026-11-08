using IcarusStarlink.PakIO;
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
        _stagedPakPath = Path.Combine(_tempDir, "Staged_Build", "zz_zz_ISL-Merged_9999_P.pak");
        // Same deterministic sibling path InstallAsync itself now derives from stagedPakPath —
        // no longer a separate parameter, so this is just where the fixture file needs to sit.
        _stagedManifestPath = Path.Combine(_tempDir, "Staged_Build", "ISL-Merged.txt");
        _fakeContentPath = Path.Combine(_tempDir, "FakeIcarusContent");
        _backupDirectory = Path.Combine(_tempDir, "Backups");

        Directory.CreateDirectory(Path.GetDirectoryName(_stagedPakPath)!);
        File.WriteAllText(_stagedPakPath, "staged pak v1 bytes");
        File.WriteAllText(_stagedManifestPath, "Includes the following mods:\nMod A");
    }

    /// <summary>The fake game has no ArcFission Library, so the merge goes in under the plain name.</summary>
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
        // The manifest is found beside the staged pak; nothing passes its path in.
        Assert.Equal(await File.ReadAllTextAsync(_stagedManifestPath), await File.ReadAllTextAsync(TargetManifestPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(TargetPakPath)!, "*.tmp"));
    }

    [Fact]
    public async Task InstallAsync_NoManifestAlongsideThePak_StillInstallsThePak()
    {
        File.Delete(_stagedManifestPath);

        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.True(File.Exists(result.InstalledPakPath));
        Assert.False(File.Exists(TargetManifestPath));
    }

    /// <summary>No list beside the staged pak: the game's old list is backed up and removed, so "In game" doesn't read the previous build's mods as this one's.</summary>
    [Fact]
    public async Task InstallAsync_NoManifestAlongsideThePak_TheGamesOldListGoes()
    {
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        Assert.True(File.Exists(TargetManifestPath));
        File.Delete(_stagedManifestPath);

        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.False(File.Exists(TargetManifestPath));
        Assert.Contains(Directory.GetFiles(_backupDirectory, "ISL-Merged_*.txt", SearchOption.AllDirectories), _ => true);
        Assert.Empty((await _service.GetInstalledStateAsync(_fakeContentPath)).ModNames);
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
    public async Task InstallAsync_FourInstallsOverExistingTarget_KeepsOnlyThreeMostRecentBackups()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPakPath)!);
        await File.WriteAllTextAsync(TargetPakPath, "initial pak bytes");

        for (var i = 1; i <= 4; i++)
        {
            await File.WriteAllTextAsync(_stagedPakPath, $"staged pak v{i} bytes");
            // No wait between installs: same-second backups get a _2, _3… suffix and are ordered by it.
            await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        }

        // Backed up: the initial pak, then v1, v2, v3 (each replaced by the next install); the newest three stay.
        var backups = Directory.GetFiles(_backupDirectory, "ISL-Merged_P_*.pak").Select(File.ReadAllText).Order().ToList();
        Assert.Equal(["staged pak v1 bytes", "staged pak v2 bytes", "staged pak v3 bytes"], backups);
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

    /// <summary>
    /// ArcFission Library writes its own data pak at game start; its rows are merged in and the
    /// merge outranks it, so it stays (deleting it only makes the library write it again). The
    /// merged pak's old name is cleared like any other leftover.
    /// </summary>
    /// <summary>Each ArcFission rebuild writes the next number and the old one stays locked while the game runs: Install keeps the newest and removes the rest (not backed up: the library rebuilds from the game).</summary>
    [Fact]
    public async Task InstallAsync_KeepsOnlyTheNewestArcFissionDataPak()
    {
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        foreach (var name in new[] { "ZZ_ArcFission_Data_1_P.pak", "ZZ_ArcFission_Data_2_P.pak", "ZZ_ArcFission_Data_10_P.pak" })
        {
            await File.WriteAllTextAsync(Path.Combine(modsDirectory, name), "library data");
        }

        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.Equal(["ZZ_ArcFission_Data_10_P.pak"], Directory.GetFiles(modsDirectory, "ZZ_ArcFission_*").Select(Path.GetFileName));
        Assert.Empty(Directory.Exists(_backupDirectory) ? Directory.GetFiles(_backupDirectory, "ZZ_ArcFission_*") : []);
    }

    /// <summary>
    /// The highest number is the copy the game loads and the one the library writes next above, so
    /// it's kept even when the library's state.lua names an older one. A stale state.lua is real: the
    /// game's ArcFissionLibrary folder (state.lua with it) is copied onto a dedicated server, or an
    /// older copy of the folder is restored. Keeping the state's older pak there made the library take
    /// it as up to date (same fingerprint, no higher pak left) and serve its old tables for good;
    /// keeping the highest one makes it rebuild once instead.
    /// </summary>
    [Fact]
    public async Task InstallAsync_KeepsTheHighestArcFissionDataPak_EvenWhenAStaleStateLuaNamesAnOlderOne()
    {
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        foreach (var name in new[] { "ZZ_ArcFission_Data_1_P.pak", "ZZ_ArcFission_Data_2_P.pak", "ZZ_ArcFission_Data_3_P.pak" })
        {
            await File.WriteAllTextAsync(Path.Combine(modsDirectory, name), "library data");
        }

        var library = Path.Combine(Path.GetDirectoryName(_fakeContentPath.TrimEnd('\\', '/'))!, "Binaries", "Win64", "ue4ss", "Mods", "ArcFissionLibrary");
        Directory.CreateDirectory(library);
        await File.WriteAllTextAsync(Path.Combine(library, "enabled.txt"), "");
        await File.WriteAllTextAsync(Path.Combine(library, "state.lua"), "return {\n    [\"gen\"] = 1,\n    [\"library\"] = \"1.3.0\",\n}\n");

        Assert.Equal(["ZZ_ArcFission_Data_1_P.pak", "ZZ_ArcFission_Data_2_P.pak"], InstallManifestNames.OlderArcFissionDataPaks(_fakeContentPath));

        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.Equal(["ZZ_ArcFission_Data_3_P.pak"], Directory.GetFiles(modsDirectory, "ZZ_ArcFission_*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task InstallAsync_LeavesArcFissionsOwnDataPak_ButClearsTheOldMergedPakName()
    {
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        var arcFission = Path.Combine(modsDirectory, "ZZ_ArcFission_Data_3_P.pak");
        var oldMerge = Path.Combine(modsDirectory, "ISL-Merged_9999_P.pak");
        await File.WriteAllTextAsync(arcFission, "library data");
        await File.WriteAllTextAsync(oldMerge, "old merge");

        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.True(File.Exists(arcFission));
        Assert.False(File.Exists(oldMerge));
    }

    /// <summary>Turns ArcFission Library on in the fake game's UE4SS (a folder named ArcFissionLibrary with enabled.txt).</summary>
    private void TurnOnArcFissionLibrary()
    {
        var library = Path.Combine(Path.GetDirectoryName(_fakeContentPath.TrimEnd('\\', '/'))!, "Binaries", "Win64", "ue4ss", "Mods", "ArcFissionLibrary");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "enabled.txt"), "");
    }

    /// <summary>
    /// zz_zz_…_9999 only while ArcFission Library is on (its data pak holds whole tables the merge
    /// must outrank); the plain name otherwise. Switching either way leaves exactly one merged pak:
    /// the copy under the other name is backed up and cleared.
    /// </summary>
    [Fact]
    public async Task InstallAsync_NamesTheMergeByWhetherArcFissionLibraryIsOn_AndClearsTheOtherName()
    {
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        var plain = Path.Combine(modsDirectory, "ISL-Merged_P.pak");
        var arcFissionName = Path.Combine(modsDirectory, "zz_zz_ISL-Merged_9999_P.pak");

        var first = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        Assert.Equal(plain, first.InstalledPakPath);
        Assert.False(File.Exists(arcFissionName));

        TurnOnArcFissionLibrary();
        var second = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        Assert.Equal(arcFissionName, second.InstalledPakPath);
        Assert.False(File.Exists(plain));
        Assert.Single(Directory.GetFiles(_backupDirectory, "ISL-Merged_P_*.pak"));
        Assert.Equal(["ISL-Merged.txt", "zz_zz_ISL-Merged_9999_P.pak"], Directory.GetFiles(modsDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NewestArcFissionDataPak_IsTheHighestNumber_AndOnlyArcFissionsFilesCount()
    {
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        foreach (var name in new[] { "ZZ_ArcFission_Data_2_P.pak", "ZZ_ArcFission_Data_10_P.pak", "ZZ_ArcFission_Data_9_P.pak", "ZZ_ArcFission_Other_99_P.pak", "zz_zz_ISL-Merged_9999_P.pak" })
        {
            File.WriteAllText(Path.Combine(modsDirectory, name), "x");
        }

        Assert.Equal("ZZ_ArcFission_Data_10_P.pak", Path.GetFileName(InstallManifestNames.NewestArcFissionDataPak(modsDirectory)));
        Assert.Null(InstallManifestNames.NewestArcFissionDataPak(Path.Combine(_fakeContentPath, "nowhere")));
        Assert.True(InstallManifestNames.IsArcFissionDataPak("zz_arcfission_data_1_p.pak"));
        Assert.False(InstallManifestNames.IsArcFissionDataPak("ZZ_ArcFission_Data_1_P.pak.disabled"));
    }

    /// <summary>The merged pak's number is what makes the game load it after every other mods-folder pak.</summary>
    [Fact]
    public void TheMergedPakName_LoadsLast_ByNumberAndByName()
    {
        var match = System.Text.RegularExpressions.Regex.Match(InstallManifestNames.MergedPakFileName, @"_(\d+)_P\.pak$");
        Assert.True(match.Success);
        Assert.True(int.Parse(match.Groups[1].Value) >= 1000);

        // Paks of equal priority load in name order: the merge sorts after ArcFission's and other
        // "ZZ_" mods however the names are compared.
        foreach (var other in new[] { "ZZ_ArcFission_Data_1_P.pak", "ZZ_WirelessPower_Icon_P.pak", "zz_lumberyard_p.pak", "ISL-Merged_P.pak" })
        {
            Assert.True(string.Compare(InstallManifestNames.MergedPakFileName, other, StringComparison.Ordinal) > 0, other);
            Assert.True(string.Compare(InstallManifestNames.MergedPakFileName, other, StringComparison.OrdinalIgnoreCase) > 0, other);
        }
    }

    [Fact]
    public async Task InstallAsync_UnrelatedFileAlreadyInModsFolder_ThisAppsOwnFilesAreReplacedNotCleared()
    {
        // The app's own pak and list go through backup-and-replace (one backup each), not the
        // stale-file clear-out the unrelated pak gets.
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        await File.WriteAllTextAsync(Path.Combine(modsDirectory, "SomeOtherMod_P.pak"), "unrelated");
        await File.WriteAllTextAsync(TargetPakPath, "old pak bytes");
        await File.WriteAllTextAsync(TargetManifestPath, "Includes the following mods:\nOld Mod");

        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(result.InstalledPakPath));
        Assert.Equal("old pak bytes", await File.ReadAllTextAsync(result.BackupPakPath!));
        Assert.Single(Directory.GetFiles(_backupDirectory, "ISL-Merged_P_*.pak"));
        Assert.Single(Directory.GetFiles(_backupDirectory, "ISL-Merged_*.txt"));
        Assert.Contains("Mod A", await File.ReadAllTextAsync(TargetManifestPath));
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
        var removed = await _service.RemoveInstalledPakAsync(_fakeContentPath, _backupDirectory);

        Assert.False(removed);
        Assert.False(Directory.Exists(_backupDirectory));
    }

    [Fact]
    public async Task RemoveInstalledPakAsync_AfterInstall_DeletesThePakAndManifestAfterBackingThemUp()
    {
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        var removed = await _service.RemoveInstalledPakAsync(_fakeContentPath, _backupDirectory);

        Assert.True(removed);
        Assert.False(File.Exists(TargetPakPath));
        Assert.False(File.Exists(TargetManifestPath));
        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(Directory.GetFiles(_backupDirectory, "ISL-Merged_P_*.pak").Single()));
    }

    [Fact]
    public async Task RemoveInstalledPakAsync_LegacyNamedMergedPak_IsRemovedTooAfterBackingItUp()
    {
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        // The merge under its other name (installed while ArcFission Library was on) goes too.
        var legacyPakPath = Path.Combine(modsDirectory, "zz_zz_ISL-Merged_9999_P.pak");
        await File.WriteAllTextAsync(legacyPakPath, "old merged pak");

        var removed = await _service.RemoveInstalledPakAsync(_fakeContentPath, _backupDirectory);

        Assert.True(removed);
        Assert.False(File.Exists(legacyPakPath));
        Assert.Equal("old merged pak", await File.ReadAllTextAsync(Directory.GetFiles(_backupDirectory, "zz_zz_ISL-Merged_9999_P_*.pak").Single()));
    }

    [Fact]
    public async Task InstallAsync_OldMergedPakBackupsUnderEarlierNames_KeepsOnlyTheNewestThreeOverall()
    {
        Directory.CreateDirectory(_backupDirectory);
        foreach (var name in new[]
                 {
                     "ISL-Merged_P_20260101-100000.pak", "ISL-Merged_P_20260102-100000.pak", "ISL-Merged_P_20260103-100000.pak",
                     "ISL-Merged_9999_P_20260104-100000.pak", "zz_zz_ISL-Merged_9999_P_20260105-100000.pak",
                     "ISL-Merged_20260101-100000.txt", "ISL-Merged_20260102-100000.txt", "ISL-Merged_20260103-100000.txt",
                 })
        {
            await File.WriteAllTextAsync(Path.Combine(_backupDirectory, name), "old backup");
        }

        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        await File.WriteAllTextAsync(_stagedPakPath, "staged pak v2 bytes");
        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        var pakBackups = Directory.GetFiles(_backupDirectory, "*.pak").Select(Path.GetFileName).ToList();
        Assert.Equal(3, pakBackups.Count);
        Assert.Contains(Path.GetFileName(result.BackupPakPath), pakBackups);
        Assert.Contains("ISL-Merged_9999_P_20260104-100000.pak", pakBackups);
        Assert.Contains("zz_zz_ISL-Merged_9999_P_20260105-100000.pak", pakBackups);
        Assert.Equal(3, Directory.GetFiles(_backupDirectory, "*.txt").Length);
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

    /// <summary>A leftover ArcFission data pak (the library itself is gone) is backed up and removed when asked.</summary>
    [Fact]
    public async Task InstallAsync_NotKeepingArcFissionsDataPak_BacksItUpAndRemovesIt()
    {
        var modsDirectory = Path.Combine(_fakeContentPath, "Paks", "mods");
        Directory.CreateDirectory(modsDirectory);
        var arcFission = Path.Combine(modsDirectory, "ZZ_ArcFission_Data_3_P.pak");
        await File.WriteAllTextAsync(arcFission, "old library data");

        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory, keepArcFissionDataPak: false);

        Assert.False(File.Exists(arcFission));
        Assert.Single(Directory.GetFiles(_backupDirectory, "ZZ_ArcFission_Data_3_P_*.pak"));
    }

    /// <summary>ArcFission Library counts as installed while its folder is in ue4ss\Mods with enabled.txt, or listed on in mods.txt.</summary>
    [Fact]
    public void IsArcFissionLibraryActive_NeedsTheLibraryFolderTurnedOn()
    {
        var modsFolder = Path.Combine(_tempDir, "Binaries", "Win64", "ue4ss", "Mods");
        Assert.False(InstallManifestNames.IsArcFissionLibraryActive(_fakeContentPath));

        Directory.CreateDirectory(Path.Combine(modsFolder, "ArcFissionLibrary"));
        Assert.False(InstallManifestNames.IsArcFissionLibraryActive(_fakeContentPath));

        File.WriteAllText(Path.Combine(modsFolder, "mods.txt"), "ArcFissionLibrary : 1\r\nKeybinds : 1\r\n");
        Assert.True(InstallManifestNames.IsArcFissionLibraryActive(_fakeContentPath));

        File.WriteAllText(Path.Combine(modsFolder, "mods.txt"), "ArcFissionLibrary : 0\r\n");
        Assert.False(InstallManifestNames.IsArcFissionLibraryActive(_fakeContentPath));

        // UE4SS starts a mod from any of its "1" lines, so an earlier 0 doesn't keep it off.
        File.WriteAllText(Path.Combine(modsFolder, "mods.txt"), "ArcFissionLibrary : 0\r\nArcFissionLibrary : 1\r\n");
        Assert.True(InstallManifestNames.IsArcFissionLibraryActive(_fakeContentPath));

        File.WriteAllText(Path.Combine(modsFolder, "mods.txt"), "ArcFissionLibrary : 0\r\n");
        File.WriteAllText(Path.Combine(modsFolder, "ArcFissionLibrary", "enabled.txt"), "");
        Assert.True(InstallManifestNames.IsArcFissionLibraryActive(_fakeContentPath));
        Assert.Null(InstallManifestNames.MisnamedArcFissionLibraryFolder(_fakeContentPath));
    }

    /// <summary>
    /// The ArcFission mods register in Mods/ArcFissionLibrary/registry.txt (LIBRARY_FOLDER in their
    /// arcfission.lua): a library turned on in a renamed folder hears from no mod and builds no rows,
    /// so it doesn't count as installed, and the renamed folder is named so the user can fix it.
    /// </summary>
    [Fact]
    public void IsArcFissionLibraryActive_ALibraryInARenamedFolder_DoesntCount_AndIsNamed()
    {
        var renamed = Path.Combine(_tempDir, "Binaries", "Win64", "ue4ss", "Mods", "ArcFissionLibrary-1.3.0");
        Directory.CreateDirectory(renamed);
        File.WriteAllText(Path.Combine(renamed, "enabled.txt"), "");

        Assert.False(InstallManifestNames.IsArcFissionLibraryActive(_fakeContentPath));
        Assert.Equal("ArcFissionLibrary-1.3.0", InstallManifestNames.MisnamedArcFissionLibraryFolder(_fakeContentPath));
    }

    /// <summary>Lists the kept builds and restores the newest, as the picker does when the player takes its first row; null when none is kept.</summary>
    private async Task<PreviousInstall?> RestoreNewestAsync(InstallService service) =>
        (await service.ListPreviousInstallsAsync(_backupDirectory)) is [var newest, ..]
            ? await service.RestoreInstallAsync(_fakeContentPath, _backupDirectory, newest)
            : null;

    /// <summary>Restore previous install puts the last pak and its mod list back, after backing up what's installed now, and can be undone the same way.</summary>
    [Fact]
    public async Task RestorePreviousInstallAsync_PutsTheLastBuildBack_AndBacksUpTheCurrentOne()
    {
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        await File.WriteAllTextAsync(_stagedPakPath, "staged pak v2 bytes");
        await File.WriteAllTextAsync(_stagedManifestPath, "Includes the following mods:\nMod B");
        // A second apart, as installs are in real use: a mod list backup is paired with its pak by time.
        await Task.Delay(1100);
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        await Task.Delay(1100);

        var restored = await RestoreNewestAsync(_service);

        Assert.NotNull(restored);
        Assert.NotNull(restored.ManifestBackupPath);
        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(TargetPakPath));
        Assert.Equal(["Mod A"], (await _service.GetInstalledStateAsync(_fakeContentPath)).ModNames);

        // Again: the copy of v2 made just now is the newest backup, so it comes back.
        await RestoreNewestAsync(_service);
        Assert.Equal("staged pak v2 bytes", await File.ReadAllTextAsync(TargetPakPath));
        Assert.Equal(["Mod B"], (await _service.GetInstalledStateAsync(_fakeContentPath)).ModNames);
    }

    // ---- Settings › Maintenance › Backups to keep › Merged pak installs ----

    private sealed class FakeSettings : IcarusStarlink.Core.Settings.ISettingsService
    {
        public IcarusStarlink.Core.Settings.AppSettings Current { get; } = new();

        public bool Save() => true;
    }

    private static InstallService ServiceKeeping(int mergedPakBackupsKept)
    {
        var settings = new FakeSettings();
        settings.Current.MergedPakBackupsKept = mergedPakBackupsKept;
        return new InstallService(settings);
    }

    private List<string> PakBackupContents() =>
        Directory.GetFiles(_backupDirectory, "ISL-Merged_P_*.pak").Select(File.ReadAllText).Order().ToList();

    /// <summary>More than FolderBackup's own 5 by one name: the per-name trim mustn't cut below the setting.</summary>
    [Fact]
    public async Task InstallAsync_MergedPakBackupsKeptSetting_KeepsThatMany()
    {
        var service = ServiceKeeping(7);
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPakPath)!);
        await File.WriteAllTextAsync(TargetPakPath, "initial pak bytes");

        for (var i = 1; i <= 10; i++)
        {
            await File.WriteAllTextAsync(_stagedPakPath, $"staged pak v{i:00} bytes");
            await service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        }

        // Backed up: the initial pak, then v01..v09; the newest seven stay.
        Assert.Equal([.. Enumerable.Range(3, 7).Select(i => $"staged pak v{i:00} bytes")], PakBackupContents());
        Assert.Equal(7, Directory.GetFiles(_backupDirectory, "ISL-Merged_*.txt").Length);
    }

    /// <summary>0 or a negative count (a hand-edited settings.json) still keeps the newest backup.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(1)]
    public async Task InstallAsync_ZeroOrNegativeSetting_KeepsTheNewestBackup(int setting)
    {
        var service = ServiceKeeping(setting);
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPakPath)!);
        await File.WriteAllTextAsync(TargetPakPath, "initial pak bytes");

        for (var i = 1; i <= 3; i++)
        {
            await File.WriteAllTextAsync(_stagedPakPath, $"staged pak v{i} bytes");
            await service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        }

        Assert.Equal(["staged pak v2 bytes"], PakBackupContents());
    }

    /// <summary>With only 1 kept, Restore previous install still has the backup it restores: backing up what's installed first must not trim it away. The restored build stays kept too, beside the newest.</summary>
    [Fact]
    public async Task RestorePreviousInstallAsync_KeepingOne_StillRestoresThePreviousPak()
    {
        var service = ServiceKeeping(1);
        await service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
        await File.WriteAllTextAsync(_stagedPakPath, "staged pak v2 bytes");
        await service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);

        var restored = await RestoreNewestAsync(service);

        Assert.NotNull(restored);
        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(TargetPakPath));
        Assert.Equal(["staged pak v1 bytes", "staged pak v2 bytes"], PakBackupContents());
    }

    /// <summary>Installs builds 1..count (mod list "Mod 1".."Mod n"), a second apart as in real use, so each list pairs with its pak by time.</summary>
    private async Task InstallBuildsAsync(InstallService service, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            await File.WriteAllTextAsync(_stagedPakPath, $"build {i}");
            await File.WriteAllTextAsync(_stagedManifestPath, $"Includes the following mods:\nMod {i}");
            await service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory);
            await Task.Delay(1100);
        }
    }

    /// <summary>The player picks a kept build that isn't the newest: that one goes in, with its own mod list, and what was installed is backed up.</summary>
    [Fact]
    public async Task RestoreInstallAsync_AnOlderBuild_PutsThatOneBack()
    {
        await InstallBuildsAsync(_service, 3);
        var kept = await _service.ListPreviousInstallsAsync(_backupDirectory);
        Assert.Equal(["Mod 2", "Mod 1"], kept.Select(p => p.ModNames!.Single()));

        var restored = await _service.RestoreInstallAsync(_fakeContentPath, _backupDirectory, kept[1]);

        Assert.NotNull(restored);
        Assert.Equal("build 1", await File.ReadAllTextAsync(TargetPakPath));
        Assert.Equal(["Mod 1"], (await _service.GetInstalledStateAsync(_fakeContentPath)).ModNames);
        var now = await _service.ListPreviousInstallsAsync(_backupDirectory);
        Assert.Equal("build 3", await File.ReadAllTextAsync(now[0].PakBackupPath));
        Assert.Equal(["Mod 3"], now[0].ModNames!);
    }

    /// <summary>
    /// The chosen build is the oldest one kept: backing up what's installed (and the prune after)
    /// must not delete it first, at the default 3 and at 1. It stays kept after the restore too.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task RestoreInstallAsync_TheOldestKeptBuild_SurvivesThePrune(int keep)
    {
        var service = ServiceKeeping(keep);
        await InstallBuildsAsync(service, keep + 1);
        var kept = await service.ListPreviousInstallsAsync(_backupDirectory);
        Assert.Equal(keep, kept.Count);
        var chosen = kept[^1];

        var restored = await service.RestoreInstallAsync(_fakeContentPath, _backupDirectory, chosen);

        Assert.NotNull(restored);
        Assert.Equal("build 1", await File.ReadAllTextAsync(TargetPakPath));
        Assert.Equal(["Mod 1"], (await service.GetInstalledStateAsync(_fakeContentPath)).ModNames);
        Assert.True(File.Exists(chosen.PakBackupPath));
        Assert.True(File.Exists(chosen.ManifestBackupPath));
        Assert.Contains($"build {keep + 1}", PakBackupContents());
    }

    /// <summary>A build whose backup was deleted since the list was read: nothing is changed.</summary>
    [Fact]
    public async Task RestoreInstallAsync_BackupGone_ReturnsNullAndTouchesNothing()
    {
        await InstallBuildsAsync(_service, 2);
        var chosen = (await _service.ListPreviousInstallsAsync(_backupDirectory))[0];
        File.Delete(chosen.PakBackupPath);

        Assert.Null(await _service.RestoreInstallAsync(_fakeContentPath, _backupDirectory, chosen));
        Assert.Equal("build 2", await File.ReadAllTextAsync(TargetPakPath));
        Assert.Empty(PakBackupContents());
    }

    /// <summary>Only the chosen build's mod list was deleted since the list was read: the pak goes in, and what's returned says it has no list.</summary>
    [Fact]
    public async Task RestoreInstallAsync_ModListGone_RestoresThePakAndSaysThereIsNoList()
    {
        await InstallBuildsAsync(_service, 2);
        var chosen = (await _service.ListPreviousInstallsAsync(_backupDirectory))[0];
        File.Delete(chosen.ManifestBackupPath!);

        var restored = await _service.RestoreInstallAsync(_fakeContentPath, _backupDirectory, chosen);

        Assert.NotNull(restored);
        Assert.Null(restored.ManifestBackupPath);
        Assert.Equal("build 1", await File.ReadAllTextAsync(TargetPakPath));
        Assert.False(File.Exists(TargetManifestPath));
    }

    /// <summary>After a restore, the build put back is still listed (it's kept), marked as the one in the game; the others aren't.</summary>
    [Fact]
    public async Task ListPreviousInstallsAsync_MarksTheBuildInTheGame()
    {
        await InstallBuildsAsync(_service, 3);
        var chosen = (await _service.ListPreviousInstallsAsync(_backupDirectory, _fakeContentPath))[1];
        Assert.False(chosen.IsInGame);
        await _service.RestoreInstallAsync(_fakeContentPath, _backupDirectory, chosen);

        var now = await _service.ListPreviousInstallsAsync(_backupDirectory, _fakeContentPath);

        Assert.Equal([chosen.PakBackupPath], now.Where(p => p.IsInGame).Select(p => p.PakBackupPath));
        Assert.All(await _service.ListPreviousInstallsAsync(_backupDirectory), p => Assert.False(p.IsInGame));
    }

    // ---- backUpWhatsThere (Find the broken mod: only the first round backs up) ----

    [Fact]
    public async Task InstallAsync_NotBackingUp_ReplacesThePakWithoutABackup_ButStillBacksUpOtherFiles()
    {
        var modsDirectory = Path.GetDirectoryName(TargetPakPath)!;
        Directory.CreateDirectory(modsDirectory);
        await File.WriteAllTextAsync(TargetPakPath, "a round's pak");
        await File.WriteAllTextAsync(TargetManifestPath, "Includes the following mods:\nRound");
        await File.WriteAllTextAsync(Path.Combine(modsDirectory, "SomeoneElses_P.pak"), "player's own pak");

        var result = await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory, backUpWhatsThere: false);

        Assert.Null(result.BackupPakPath);
        Assert.Equal("staged pak v1 bytes", await File.ReadAllTextAsync(TargetPakPath));
        Assert.Empty(PakBackupContents());
        Assert.Empty(Directory.GetFiles(_backupDirectory, "ISL-Merged_*.txt"));
        Assert.Equal("player's own pak", await File.ReadAllTextAsync(Directory.GetFiles(_backupDirectory, "SomeoneElses_P_*.pak").Single()));
    }

    [Fact]
    public async Task RemoveInstalledPakAsync_NotBackingUp_RemovesTheRoundPakWithoutABackup_ButBacksUpAnEarlierName()
    {
        var modsDirectory = Path.GetDirectoryName(TargetPakPath)!;
        Directory.CreateDirectory(modsDirectory);
        await File.WriteAllTextAsync(TargetPakPath, "a round's pak");
        await File.WriteAllTextAsync(TargetManifestPath, "Includes the following mods:\nRound");
        await File.WriteAllTextAsync(Path.Combine(modsDirectory, "zz_zz_ISL-Merged_9999_P.pak"), "old merged pak");

        Assert.True(await _service.RemoveInstalledPakAsync(_fakeContentPath, _backupDirectory, backUpWhatsThere: false));

        Assert.False(File.Exists(TargetPakPath));
        Assert.False(File.Exists(TargetManifestPath));
        Assert.Empty(PakBackupContents());
        Assert.Empty(Directory.GetFiles(_backupDirectory, "ISL-Merged_*.txt"));
        Assert.Equal("old merged pak", await File.ReadAllTextAsync(Directory.GetFiles(_backupDirectory, "zz_zz_ISL-Merged_9999_P_*.pak").Single()));
    }

    /// <summary>
    /// The bug: every finder round backed up the pak in the game, so after 3 rounds the player's
    /// real pre-search install was pruned out of Restore previous install. Now only the first
    /// change backs up: after many rounds (and a no-mods round, and putting the mods back) the
    /// real install is still the newest backup.
    /// </summary>
    [Fact]
    public async Task FinderRounds_OnlyTheFirstBacksUp_ThePlayersRealInstallStaysRestorable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPakPath)!);
        await File.WriteAllTextAsync(TargetPakPath, "player's real pak");
        await File.WriteAllTextAsync(TargetManifestPath, "Includes the following mods:\nReal");

        await File.WriteAllTextAsync(_stagedPakPath, "round 1 pak");
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory, backUpWhatsThere: true);
        for (var round = 2; round <= 6; round++)
        {
            await File.WriteAllTextAsync(_stagedPakPath, $"round {round} pak");
            await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory, backUpWhatsThere: false);
        }

        await _service.RemoveInstalledPakAsync(_fakeContentPath, _backupDirectory, backUpWhatsThere: false);
        await File.WriteAllTextAsync(_stagedPakPath, "put back pak");
        await _service.InstallAsync(_stagedPakPath, _fakeContentPath, _backupDirectory, backUpWhatsThere: false);

        Assert.Equal(["player's real pak"], PakBackupContents());
        var previous = (await _service.ListPreviousInstallsAsync(_backupDirectory))[0];
        Assert.Equal("player's real pak", await File.ReadAllTextAsync(previous.PakBackupPath));
        Assert.Equal("Includes the following mods:\nReal", await File.ReadAllTextAsync(previous.ManifestBackupPath!));
    }

    [Fact]
    public async Task ListPreviousInstallsAsync_NoBackup_IsEmpty()
    {
        Assert.Empty(await _service.ListPreviousInstallsAsync(_backupDirectory, _fakeContentPath));
        Assert.False(File.Exists(TargetPakPath));
    }

    /// <summary>A chosen build outside the Backups folder (a caller's mistake) is refused before anything is touched.</summary>
    [Fact]
    public async Task RestoreInstallAsync_APakOutsideTheBackupsFolder_ReturnsNullAndTouchesNothing()
    {
        await InstallBuildsAsync(_service, 2);
        var elsewhere = Path.Combine(_tempDir, "ISL-Merged_P_20261016-100000.pak");
        await File.WriteAllTextAsync(elsewhere, "not a backup");

        Assert.Null(await _service.RestoreInstallAsync(_fakeContentPath, _backupDirectory, new PreviousInstall(elsewhere, null, DateTime.UtcNow)));
        Assert.Equal("build 2", await File.ReadAllTextAsync(TargetPakPath));
        Assert.Equal(["build 1"], PakBackupContents());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
