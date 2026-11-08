using System.IO.Compression;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>Restore through a staging folder, and how a merged pak replaces the server's.</summary>
public sealed partial class ServerFilesTests
{
    /// <summary>
    /// Regression: a backup that broke part way used to be extracted straight into Saved, so the
    /// worlds before the bad entry were already the backup's and the rest the current ones. Now
    /// nothing in Saved changes until every entry is read.
    /// </summary>
    [Fact]
    public void Restore_ABadEntry_LeavesSavedUnchanged()
    {
        var install = Path.Combine(_root, "Server");
        var prospects = DedicatedServerLayout.ProspectsFolder(install, null);
        Directory.CreateDirectory(prospects);
        File.WriteAllText(Path.Combine(prospects, "A.json"), "current");
        var backups = Path.Combine(_root, "Backups");
        var zip = Path.Combine(_root, "broken.zip");
        WriteZipWithAFileWhereAFolderIs(zip);

        var ex = Assert.Throws<ServerRestoreException>(() => ServerBackups.Restore(zip, install, null, backups));

        Assert.False(ex.SavedChanged);
        Assert.True(File.Exists(ex.SafetyBackupPath));
        Assert.Equal("current", File.ReadAllText(Path.Combine(prospects, "A.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(install, "Icarus"), "Saved.restoring-*"));
    }

    /// <summary>A whole backup still lands in Saved, and no staging folder is left beside it.</summary>
    [Fact]
    public void Restore_AGoodBackup_ReplacesTheWorldsAndLeavesNoStagingFolder()
    {
        var install = Path.Combine(_root, "Server");
        var prospects = DedicatedServerLayout.ProspectsFolder(install, null);
        Directory.CreateDirectory(prospects);
        File.WriteAllText(Path.Combine(prospects, "A.json"), "backup");
        var backups = Path.Combine(_root, "Backups");
        var zip = ServerBackups.CreateBackup(install, null, backups, "manual");
        File.WriteAllText(Path.Combine(prospects, "A.json"), "current");

        var safety = ServerBackups.Restore(zip, install, null, backups);

        Assert.True(File.Exists(safety));
        Assert.Equal("backup", File.ReadAllText(Path.Combine(prospects, "A.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(install, "Icarus"), "Saved.restoring-*"));
    }

    /// <summary>
    /// Regression: a zip with two entries whose names differ only in case staged both into the same
    /// file, then the second move found nothing to move, and a restore that had finished was reported
    /// as stopped part way.
    /// </summary>
    [Fact]
    public void Restore_TwoEntriesThatDifferOnlyInCase_FinishesWithTheLastOne()
    {
        var install = Path.Combine(_root, "Server");
        var prospects = DedicatedServerLayout.ProspectsFolder(install, null);
        Directory.CreateDirectory(prospects);
        File.WriteAllText(Path.Combine(prospects, "A.json"), "current");
        var backups = Path.Combine(_root, "Backups");
        var zip = Path.Combine(_root, "cased.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("PlayerData/DedicatedServer/Prospects/A.json").Open()))
            {
                w.Write("first");
            }

            using (var w = new StreamWriter(archive.CreateEntry("PlayerData/DedicatedServer/Prospects/a.json").Open()))
            {
                w.Write("second");
            }
        }

        var safety = ServerBackups.Restore(zip, install, null, backups);

        Assert.True(File.Exists(safety));
        Assert.Equal("second", File.ReadAllText(Path.Combine(prospects, "A.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(install, "Icarus"), "Saved.restoring-*"));
    }

    /// <summary>
    /// Regression: when Saved is a junction to another drive each move is a copy then a delete, so a
    /// failure on the first file can leave it cut off. That failure used to say Saved was unchanged
    /// because no move had finished, and the player wasn't pointed to the before-restore backup.
    /// </summary>
    [Fact]
    public void Restore_TheFirstMoveFails_SaysSavedMayHaveChanged()
    {
        var install = Path.Combine(_root, "Server");
        var prospects = DedicatedServerLayout.ProspectsFolder(install, null);
        Directory.CreateDirectory(prospects);
        var world = Path.Combine(prospects, "A.json");
        File.WriteAllText(world, "backup");
        var backups = Path.Combine(_root, "Backups");
        var zip = ServerBackups.CreateBackup(install, null, backups, "manual");

        ServerRestoreException ex;
        using (new FileStream(world, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ex = Assert.Throws<ServerRestoreException>(() => ServerBackups.Restore(zip, install, null, backups));
        }

        Assert.True(ex.SavedChanged);
        Assert.True(File.Exists(ex.SafetyBackupPath));
        Assert.Empty(Directory.GetDirectories(Path.Combine(install, "Icarus"), "Saved.restoring-*"));
    }

    /// <summary>
    /// Regression: when the new build had no ISL-Merged.txt the server's old list stayed, and Export
    /// patch for players and Compare read it as the new build's mod list.
    /// </summary>
    [Fact]
    public void InstallPak_StagedListMissing_RemovesTheServersOldList()
    {
        var install = Path.Combine(_root, "Server");
        var mods = DedicatedServerLayout.PakModsFolder(install);
        Directory.CreateDirectory(mods);
        var serverList = Path.Combine(mods, "ISL-Merged.txt");
        File.WriteAllText(serverList, "Includes the following mods:\r\nOld Mod\r\n");
        var source = Path.Combine(_root, "Staged", "ISL-Merged_P.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [2]);

        ServerModFiles.InstallPak(source, install, "ISL-Merged.txt");

        Assert.False(File.Exists(serverList));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(mods, "ISL-Merged_P.pak")));
    }

    /// <summary>The server's previous merged pak and its mod list are kept in removed-paks before the new build replaces them.</summary>
    [Fact]
    public void InstallPak_SameName_KeepsTheReplacedPakInRemovedPaks()
    {
        var install = Path.Combine(_root, "Server");
        var mods = DedicatedServerLayout.PakModsFolder(install);
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "ISL-Merged_P.pak"), [1]);
        File.WriteAllText(Path.Combine(mods, "ISL-Merged.txt"), "old list");
        var source = Path.Combine(_root, "Staged", "ISL-Merged_P.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [2]);
        File.WriteAllText(Path.Combine(_root, "Staged", "ISL-Merged.txt"), "new list");
        var backups = Path.Combine(_root, "Backups");

        ServerModFiles.InstallPak(source, install, "ISL-Merged.txt", backups);

        Assert.Equal([2], File.ReadAllBytes(Path.Combine(mods, "ISL-Merged_P.pak")));
        Assert.Equal("new list", File.ReadAllText(Path.Combine(mods, "ISL-Merged.txt")));
        var removed = Path.Combine(backups, "removed-paks");
        var kept = Assert.Single(Directory.GetFiles(removed, "*_ISL-Merged_P.pak"));
        Assert.Equal([1], File.ReadAllBytes(kept));
        Assert.Equal("old list", File.ReadAllText(Assert.Single(Directory.GetFiles(removed, "*_ISL-Merged.txt"))));
    }

    /// <summary>Installing the build the server already has keeps no copy: nothing would be lost.</summary>
    [Fact]
    public void InstallPak_SameBuildAgain_KeepsNoCopy()
    {
        var install = Path.Combine(_root, "Server");
        var mods = DedicatedServerLayout.PakModsFolder(install);
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "ISL-Merged_P.pak"), [7, 7]);
        var source = Path.Combine(_root, "Staged", "ISL-Merged_P.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [7, 7]);
        var backups = Path.Combine(_root, "Backups");

        ServerModFiles.InstallPak(source, install, "ISL-Merged.txt", backups);

        Assert.False(Directory.Exists(Path.Combine(backups, "removed-paks")));
        Assert.Equal([7, 7], File.ReadAllBytes(Path.Combine(mods, "ISL-Merged_P.pak")));
    }

    /// <summary>A world entry, then a FILE entry named like the folder that world sits in: it can't be extracted where that folder already is.</summary>
    private static void WriteZipWithAFileWhereAFolderIs(string zip)
    {
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        using (var w = new StreamWriter(archive.CreateEntry("PlayerData/DedicatedServer/Prospects/A.json").Open()))
        {
            w.Write("backup");
        }

        using (var w = new StreamWriter(archive.CreateEntry("PlayerData/DedicatedServer/Prospects").Open()))
        {
            w.Write("not a folder");
        }
    }
}
