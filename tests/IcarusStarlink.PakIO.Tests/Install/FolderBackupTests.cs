using System.Globalization;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.PakIO.Tests.Install;

public class FolderBackupTests
{
    /// <summary>
    /// The stamp in a backup's name is read back by BackupCleanup as a Gregorian date. Written with
    /// the PC's own calendar, a Thai PC named a 2026 backup "_2569…": a date 543 years out, sorted
    /// after every backup made under another setting (or by code that writes the date invariantly).
    /// </summary>
    [Fact]
    public void MakeUniqueTimestampedPath_UsesTheGregorianDate_WhateverThePcsCalendar()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");

            var path = FolderBackup.MakeUniqueTimestampedPath(directory, "ISL-Merged_9999_P", new DateTimeOffset(2026, 10, 20, 13, 5, 9, TimeSpan.Zero), ".pak");

            Assert.Equal("ISL-Merged_9999_P_20261020-130509.pak", Path.GetFileName(path));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    /// <summary>
    /// A prune deleted "_stamp" and kept "_stamp_2": the next backup in that second must be "_3",
    /// not the freed "_stamp", or it sorts as older than the copy it replaces (CI kept v1 over v2
    /// with Backups to keep at 1).
    /// </summary>
    [Fact]
    public void MakeUniqueTimestampedPath_AfterAPruneLeftAGap_NumbersPastTheHighest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "ISL-Merged_P_20261020-130509_2.pak"), "kept");

            var path = FolderBackup.MakeUniqueTimestampedPath(directory, "ISL-Merged_P", new DateTimeOffset(2026, 10, 20, 13, 5, 9, TimeSpan.Zero), ".pak");

            Assert.Equal("ISL-Merged_P_20261020-130509_3.pak", Path.GetFileName(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Newest by the name's stamp and number, not creation time (NTFS hands a re-created name the deleted file's creation time).</summary>
    [Fact]
    public void EnumerateFileBackups_NewestByName_NotByCreationTime()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var older = Path.Combine(directory, "Main_20261020-130508.json");
            var plain = Path.Combine(directory, "Main_20261020-130509.json");
            var second = Path.Combine(directory, "Main_20261020-130509_2.json");
            var tenth = Path.Combine(directory, "Main_20261020-130509_10.json");
            foreach (var (path, created) in new[] { (tenth, 1), (second, 2), (plain, 3), (older, 4) })
            {
                File.WriteAllText(path, "{}");
                File.SetCreationTimeUtc(path, new DateTime(2026, 1, 1, 0, 0, created, DateTimeKind.Utc));
            }

            Assert.Equal([tenth, second, plain, older], FolderBackup.EnumerateFileBackups(directory, "Main", ".json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsTimestampedBackupOf_MatchesOnlyThatName()
    {
        Assert.True(FolderBackup.IsTimestampedBackupOf("Foo_20261020-130509", "Foo"));
        Assert.True(FolderBackup.IsTimestampedBackupOf("Foo_20261020-130509_2.pak", "Foo", ".pak"));
        Assert.False(FolderBackup.IsTimestampedBackupOf("Foo_2_20261020-130509", "Foo"));
        Assert.False(FolderBackup.IsTimestampedBackupOf("Foo_20261020-130509.pak", "Foo"));
    }

    /// <summary>
    /// Regression: a copy that stopped partway (a full disk, a file locked with no sharing) used to
    /// stay as the newest "Mod_stamp" backup, and Restore latest backup then put that partial copy
    /// over the whole mod.
    /// </summary>
    [Fact]
    public void BackupFolder_CopyFailsPartway_LeavesNoBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "Mod");
        var backups = Path.Combine(directory, "Backups");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "b");
        try
        {
            using (new FileStream(Path.Combine(source, "sub", "b.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => FolderBackup.BackupFolder(source, backups));
            }

            Assert.Empty(FolderBackup.EnumerateFolderBackups(backups, "Mod"));
            Assert.Empty(Directory.GetFileSystemEntries(backups));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Regression: an old backup held open (FModel) or read-only made the trim after a backup throw,
    /// and Install failed with "the pak is in use" although the new backup had been made.
    /// </summary>
    [Fact]
    public void BackupFile_AnOldBackupIsHeld_StillBacksUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        var backups = Path.Combine(directory, "Backups");
        Directory.CreateDirectory(backups);
        var source = Path.Combine(directory, "X.pak");
        File.WriteAllText(source, "new");
        var oldest = Path.Combine(backups, "X_20261001-120000.pak");
        var older = Path.Combine(backups, "X_20261002-120000.pak");
        File.WriteAllText(oldest, "1");
        File.WriteAllText(older, "2");
        try
        {
            string? backup;
            using (new FileStream(oldest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                backup = FolderBackup.BackupFile(source, backups, maxBackups: 1);
            }

            Assert.NotNull(backup);
            Assert.Equal("new", File.ReadAllText(backup));
            Assert.False(File.Exists(older));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Same as the file case: one held old folder backup must not make the new backup fail.</summary>
    [Fact]
    public void BackupFolder_AnOldBackupFolderIsHeld_StillBacksUp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(directory, "Mod");
        var backups = Path.Combine(directory, "Backups");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "new");
        for (var day = 1; day <= 6; day++)
        {
            var old = Path.Combine(backups, $"Mod_2026100{day}-120000");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "a.txt"), "old");
        }

        try
        {
            using (new FileStream(Path.Combine(backups, "Mod_20261001-120000", "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                FolderBackup.BackupFolder(source, backups);
            }

            var newest = FolderBackup.EnumerateFolderBackups(backups, "Mod")[0];
            Assert.Equal("new", File.ReadAllText(Path.Combine(newest, "a.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
