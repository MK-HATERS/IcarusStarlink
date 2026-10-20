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

    [Fact]
    public void IsTimestampedBackupOf_MatchesOnlyThatName()
    {
        Assert.True(FolderBackup.IsTimestampedBackupOf("Foo_20261020-130509", "Foo"));
        Assert.True(FolderBackup.IsTimestampedBackupOf("Foo_20261020-130509_2.pak", "Foo", ".pak"));
        Assert.False(FolderBackup.IsTimestampedBackupOf("Foo_2_20261020-130509", "Foo"));
        Assert.False(FolderBackup.IsTimestampedBackupOf("Foo_20261020-130509.pak", "Foo"));
    }
}
