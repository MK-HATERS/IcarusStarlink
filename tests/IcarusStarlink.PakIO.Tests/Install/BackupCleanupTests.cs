using System.Globalization;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.PakIO.Tests.Install;

public class BackupCleanupTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _backups;

    public BackupCleanupTests()
    {
        _backups = Path.Combine(_tempDir, "Backups");
        Directory.CreateDirectory(_backups);
    }

    private string WriteFile(string relativePath, int bytes = 10)
    {
        var path = Path.Combine(_backups, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string WriteFolder(string relativePath, int bytes = 10)
    {
        WriteFile(Path.Combine(relativePath, "Scripts", "main.lua"), bytes);
        return Path.Combine(_backups, relativePath);
    }

    private IEnumerable<string> RootFileNames() => Directory.GetFiles(_backups).Select(Path.GetFileName)!;

    [Fact]
    public void PruneMergedPakBackups_CountsCurrentAndLegacyNamesTogether_KeepsNewestThree()
    {
        WriteFile("ISL-Merged_P_20261016-125929.pak");
        WriteFile("ISL-Merged_P_20261016-161819.pak");
        WriteFile("ISL-Merged_9999_P_20261016-180611.pak");
        WriteFile("zz_zz_ISL-Merged_9999_P_20261016-214049.pak");
        WriteFile("zz_zz_ISL-Merged_9999_P_20261016-220125.pak");
        WriteFile("zz_zz_ISL-Merged_9999_P_20261016-230833.pak");
        WriteFile("SomeOtherMod_P_20261016-100000.pak");

        BackupCleanup.PruneMergedPakBackups(_backups);

        Assert.Equal(
            ["SomeOtherMod_P_20261016-100000.pak", "zz_zz_ISL-Merged_9999_P_20261016-214049.pak",
             "zz_zz_ISL-Merged_9999_P_20261016-220125.pak", "zz_zz_ISL-Merged_9999_P_20261016-230833.pak"],
            RootFileNames().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PruneMergedPakBackups_NewestLegacyBackupBeatsOlderCurrentOnes()
    {
        WriteFile("zz_zz_ISL-Merged_9999_P_20261015-100000.pak");
        WriteFile("ISL-Merged_9999_P_20261016-100000.pak");

        BackupCleanup.PruneMergedPakBackups(_backups, keep: 1);

        Assert.Equal(["ISL-Merged_9999_P_20261016-100000.pak"], RootFileNames());
    }

    [Fact]
    public void CleanUp_KeepsNewestTwoOfEachSet_AndReportsWhatItFreed()
    {
        // Merged pak across three names, plus its manifest.
        WriteFile("ISL-Merged_P_20261016-125929.pak", 100);
        WriteFile("ISL-Merged_9999_P_20261016-180611.pak", 100);
        WriteFile("zz_zz_ISL-Merged_9999_P_20261016-214049.pak", 100);
        WriteFile("zz_zz_ISL-Merged_9999_P_20261016-220125.pak", 100);
        WriteFile("ISL-Merged_20261016-214051.txt", 5);
        WriteFile("ISL-Merged_20261016-220127.txt", 5);
        WriteFile("ISL-Merged_20261016-230833.txt", 5);
        // Mod folder backups: three of one mod, one of another.
        WriteFolder(Path.Combine("Mods", "Homestead_20261016-153136"), 50);
        WriteFolder(Path.Combine("Mods", "Homestead_20261016-160808"), 50);
        WriteFolder(Path.Combine("Mods", "Homestead_20261016-231548"), 50);
        WriteFolder(Path.Combine("Mods", "PetMover_20261016-160945"), 50);
        // Server: three zips (different reasons, one set) and a removed pak.
        WriteFile(Path.Combine("Server", "Server1_dc7ea824", "2026-10-15_210428_before-mods.zip"), 20);
        WriteFile(Path.Combine("Server", "Server1_dc7ea824", "2026-10-16_153543_before-restore.zip"), 20);
        WriteFile(Path.Combine("Server", "Server1_dc7ea824", "2026-10-16_172458_before-mods.zip"), 20);
        WriteFile(Path.Combine("Server", "Server1_dc7ea824", "removed-paks", "2026-10-16_115951_ZZ_ArcFission_Data_1_P.pak"), 20);

        var result = BackupCleanup.CleanUp(_backups);

        Assert.Equal(5, result.DeletedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(100 + 100 + 5 + 50 + 20, result.FreedBytes);
        Assert.Equal(
            ["ISL-Merged_20261016-220127.txt", "ISL-Merged_20261016-230833.txt",
             "zz_zz_ISL-Merged_9999_P_20261016-214049.pak", "zz_zz_ISL-Merged_9999_P_20261016-220125.pak"],
            RootFileNames().Order(StringComparer.Ordinal));
        Assert.Equal(
            ["Homestead_20261016-160808", "Homestead_20261016-231548", "PetMover_20261016-160945"],
            Directory.GetDirectories(Path.Combine(_backups, "Mods")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["2026-10-16_153543_before-restore.zip", "2026-10-16_172458_before-mods.zip"],
            Directory.GetFiles(Path.Combine(_backups, "Server", "Server1_dc7ea824")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(_backups, "Server", "Server1_dc7ea824", "removed-paks", "2026-10-16_115951_ZZ_ArcFission_Data_1_P.pak")));
    }

    [Fact]
    public void CleanUp_NeverDeletesTheNewestBackup_EvenWhenAskedToKeepNone()
    {
        WriteFile("ISL-Merged_20261016-214051.txt");
        WriteFile("ISL-Merged_20261016-230833.txt");

        BackupCleanup.CleanUp(_backups, keepPerSet: 0);

        Assert.Equal(["ISL-Merged_20261016-230833.txt"], RootFileNames());
    }

    [Fact]
    public void CleanUp_LeavesFilesItDidNotNameAlone_AndNeverLooksInsideABackup()
    {
        WriteFile("notes.txt");
        WriteFile(Path.Combine("Mine", "keep-me.pak"));
        // A backup's own contents can look like backups too; only the backup as a whole counts.
        WriteFile(Path.Combine("Mods", "Foo_20261016-100000", "Bar_20261016-090000.txt"));
        WriteFile(Path.Combine("Mods", "Foo_20261016-100000", "Bar_20261016-080000.txt"));
        WriteFile(Path.Combine("Mods", "Foo_20261016-100000", "Bar_20261016-070000.txt"));

        var result = BackupCleanup.CleanUp(_backups);

        Assert.Equal(0, result.DeletedCount);
        Assert.True(File.Exists(Path.Combine(_backups, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(_backups, "Mine", "keep-me.pak")));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_backups, "Mods", "Foo_20261016-100000")).Length);
    }

    [Fact]
    public void CleanUp_SimilarModNamesAreSeparateSets()
    {
        WriteFolder(Path.Combine("Mods", "Foo_20261016-100000"));
        WriteFolder(Path.Combine("Mods", "Foo_20261016-110000"));
        WriteFolder(Path.Combine("Mods", "Foo_2_20261016-090000"));

        var result = BackupCleanup.CleanUp(_backups);

        Assert.Equal(0, result.DeletedCount);
    }

    [Fact]
    public void CleanUp_MissingFolder_DoesNothing()
    {
        var result = BackupCleanup.CleanUp(Path.Combine(_tempDir, "Nope"));

        Assert.Equal(new BackupCleanupResult(0, 0, 0), result);
    }

    [Fact]
    public void MeasureBytes_AddsUpEveryFileUnderTheFolder()
    {
        WriteFile("a.pak", 100);
        WriteFolder(Path.Combine("Mods", "Foo_20261016-100000"), 50);

        Assert.Equal(150, BackupCleanup.MeasureBytes(_backups));
        Assert.Equal(0, BackupCleanup.MeasureBytes(Path.Combine(_tempDir, "Nope")));
    }

    [Fact]
    public void FormatSize_PicksAReadableUnit()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("1 KB", BackupCleanup.FormatSize(10));
            Assert.Equal("572 MB", BackupCleanup.FormatSize(572L * 1024 * 1024));
            Assert.Equal("1.6 GB", BackupCleanup.FormatSize((long)(1.6 * 1024 * 1024 * 1024)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>A world backup made before a restore isn't lost behind newer before-mods ones: the newest of each reason stays.</summary>
    [Fact]
    public void CleanUp_ServerZips_KeepsTheNewestOfEachReasonToo()
    {
        var server = Path.Combine("Server", "Server1_dc7ea824");
        WriteFile(Path.Combine(server, "2026-10-14_100000_before-restore.zip"));
        WriteFile(Path.Combine(server, "2026-10-14_090000_before-restore.zip"));
        WriteFile(Path.Combine(server, "2026-10-15_100000_before-mods.zip"));
        WriteFile(Path.Combine(server, "2026-10-16_100000_before-mods.zip"));
        WriteFile(Path.Combine(server, "2026-10-16_110000_before-mods_2.zip"));
        WriteFile(Path.Combine(server, "2026-10-16_120000_manual.zip"));

        BackupCleanup.CleanUp(_backups, keepPerSet: 2);

        Assert.Equal(
            ["2026-10-14_100000_before-restore.zip", "2026-10-16_110000_before-mods_2.zip", "2026-10-16_120000_manual.zip"],
            Directory.GetFiles(Path.Combine(_backups, server)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    /// <summary>The merged pak's mod list backups are pruned with the paks, to the same count.</summary>
    [Fact]
    public void PruneMergedPakBackups_PrunesTheModListBackupsToTheSameCount()
    {
        foreach (var stamp in new[] { "20261016-100000", "20261016-110000", "20261016-120000", "20261016-130000" })
        {
            WriteFile($"zz_zz_ISL-Merged_9999_P_{stamp}.pak");
            WriteFile($"ISL-Merged_{stamp}.txt");
        }

        BackupCleanup.PruneMergedPakBackups(_backups, keep: 2);

        Assert.Equal(
            ["ISL-Merged_20261016-120000.txt", "ISL-Merged_20261016-130000.txt",
             "zz_zz_ISL-Merged_9999_P_20261016-120000.pak", "zz_zz_ISL-Merged_9999_P_20261016-130000.pak"],
            RootFileNames().Order(StringComparer.Ordinal));
    }

    /// <summary>The newest pak backup pairs with the mod list backed up a few seconds after it, as the user's real backups show (214049 / 214051).</summary>
    [Fact]
    public void FindNewestMergedPakBackup_PairsTheModListMadeRightAfterThePak()
    {
        WriteFile("zz_zz_ISL-Merged_9999_P_20261016-214049.pak");
        var newest = WriteFile("zz_zz_ISL-Merged_9999_P_20261016-220125.pak");
        WriteFile("ISL-Merged_20261016-214051.txt");
        var paired = WriteFile("ISL-Merged_20261016-220127.txt");
        WriteFile("ISL-Merged_20261016-235959.txt");

        var found = BackupCleanup.FindNewestMergedPakBackup(_backups);

        Assert.NotNull(found);
        Assert.Equal(newest, found.PakBackupPath);
        Assert.Equal(paired, found.ManifestBackupPath);
        Assert.Equal(new DateTime(2026, 10, 16, 22, 1, 25, DateTimeKind.Utc), found.BackedUpAtUtc);
        Assert.Null(BackupCleanup.FindNewestMergedPakBackup(Path.Combine(_tempDir, "nowhere")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
