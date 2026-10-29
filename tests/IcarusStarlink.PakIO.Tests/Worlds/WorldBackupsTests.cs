using System.IO.Compression;
using IcarusStarlink.PakIO.Worlds;

namespace IcarusStarlink.PakIO.Tests.Worlds;

/// <summary>
/// World backups (Worlds tab): listed per world and per slot, restored through a temp file after
/// backing the world up, only the chosen world out of a zip of worlds, and pruned per world.
/// Synthetic worlds only.
/// </summary>
public sealed class WorldBackupsTests : IDisposable
{
    private const string SlotA = "76561198000000001";
    private const string SlotB = "76561198000000002";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorldBackupsTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _prospects;
    private readonly string _backups;

    public WorldBackupsTests()
    {
        _prospects = Path.Combine(_root, "Prospects");
        _backups = Path.Combine(_root, "Worlds");
        Directory.CreateDirectory(_prospects);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string World(string id, int arachnophobia) =>
        "{\r\n\t\"ProspectInfo\":\r\n\t{\r\n\t\t\"ProspectID\": \"" + id + "\",\r\n\t\t\"CustomSettings\": [\r\n\t\t\t{\r\n\t\t\t\t\"SettingRowName\": \"Arachnophobia\",\r\n\t\t\t\t\"SettingValue\": "
        + arachnophobia + "\r\n\t\t\t}\r\n\t\t]\r\n\t},\r\n\t\"ProspectBlob\": {}\r\n}";

    private string WriteWorld(string name, int arachnophobia)
    {
        var path = Path.Combine(_prospects, name + ".json");
        File.WriteAllText(path, World(name, arachnophobia));
        return path;
    }

    private string FinderZip(string owner, string stamp)
    {
        Directory.CreateDirectory(_backups);
        var zip = Path.Combine(_backups, $"{WorldBackups.FindBrokenModZipBaseName(owner)}_{stamp}.zip");
        ZipFile.CreateFromDirectory(_prospects, zip);
        return zip;
    }

    [Fact]
    public void List_ShowsOnlyThisWorldsBackups_OfThisSlot()
    {
        var mine = WriteWorld("MyWorld", 0);
        var other = WriteWorld("Other", 0);
        WorldBackups.BackUp(mine, _backups, SlotA, WorldBackupKind.BeforeSettingsSave);
        WorldBackups.BackUp(other, _backups, SlotA, WorldBackupKind.BeforeSettingsSave);
        // Another slot with a world of the same name: never mixed in.
        WorldBackups.BackUp(mine, _backups, SlotB, WorldBackupKind.BeforeSettingsSave);
        // A backup from before names carried a slot can't be told apart, so it isn't offered.
        File.WriteAllText(Path.Combine(_backups, "2026-10-01_101010_123_MyWorld.json"), World("MyWorld", 1));

        var listed = WorldBackups.List(_backups, SlotA, "MyWorld.json");

        var backup = Assert.Single(listed);
        Assert.StartsWith(SlotA + "_", Path.GetFileName(backup.Path));
        Assert.Equal(WorldBackupKind.BeforeSettingsSave, backup.Kind);
        Assert.Equal("before world settings save", backup.KindText);
        Assert.Single(WorldBackups.List(_backups, SlotB, "MyWorld.json"));
    }

    [Fact]
    public void List_FindTheBrokenModZips_ListedForTheWorldsTheyHold_OfThisSlotOnly()
    {
        WriteWorld("MyWorld", 0);
        FinderZip(SlotA, "20261027-101010");
        FinderZip(SlotB, "20261027-101011");
        File.Delete(Path.Combine(_prospects, "MyWorld.json"));
        WriteWorld("Later", 0);
        FinderZip(SlotA, "20261028-101010");
        // A damaged zip is skipped, not a failure.
        File.WriteAllText(Path.Combine(_backups, $"{WorldBackups.FindBrokenModZipBaseName(SlotA)}_20261028-111111.zip"), "not a zip");

        var listed = WorldBackups.List(_backups, SlotA, "MyWorld.json");

        var zip = Assert.Single(listed);
        Assert.True(zip.IsZip);
        Assert.Equal("before Find the broken mod", zip.KindText);
        Assert.Equal(new DateTime(2026, 10, 27, 10, 10, 10), zip.TakenAt);
    }

    [Fact]
    public void List_NewestFirst_SameSecondOrderedByItsNumber()
    {
        var world = WriteWorld("MyWorld", 0);
        var first = WorldBackups.BackUp(world, _backups, SlotA, WorldBackupKind.BeforeSettingsSave);
        var second = WorldBackups.BackUp(world, _backups, SlotA, WorldBackupKind.BeforeSettingsSave);
        var third = WorldBackups.BackUp(world, _backups, SlotA, WorldBackupKind.BeforeRestore);

        var listed = WorldBackups.List(_backups, SlotA, "MyWorld.json").Select(b => b.Path).ToList();

        Assert.Equal(3, listed.Count);
        Assert.Equal(third, listed[0]);
        Assert.Equal(first, listed[^1]);
        Assert.Contains(second, listed);
    }

    [Fact]
    public void Restore_BacksTheWorldUpFirst_ThenPutsTheBackupBack_WithTheTimeOfTheRestore()
    {
        var world = WriteWorld("MyWorld", 0);
        var backupPath = WorldBackups.BackUp(world, _backups, SlotA, WorldBackupKind.BeforeSettingsSave);
        File.SetLastWriteTime(backupPath, new DateTime(2020, 1, 1));
        File.WriteAllText(world, World("MyWorld", 1));
        var backup = WorldBackups.List(_backups, SlotA, "MyWorld.json").Single();

        var started = DateTime.Now.AddSeconds(-2);
        var before = WorldBackups.Restore(backup, world, _backups, SlotA, keep: 10);

        Assert.Equal(World("MyWorld", 0), File.ReadAllText(world));
        Assert.True(File.GetLastWriteTime(world) >= started);
        Assert.NotNull(before);
        Assert.Equal(World("MyWorld", 1), File.ReadAllText(before));
        Assert.Contains(WorldBackups.List(_backups, SlotA, "MyWorld.json"), b => b.Path == before && b.Kind == WorldBackupKind.BeforeRestore);
        // No temp file left beside the world.
        Assert.Equal(["MyWorld.json"], Directory.GetFiles(_prospects).Select(Path.GetFileName));
    }

    [Fact]
    public void Restore_FromAZipOfWorlds_PutsBackOnlyTheChosenWorld()
    {
        var mine = WriteWorld("MyWorld", 0);
        var other = WriteWorld("Other", 0);
        FinderZip(SlotA, "20261027-101010");
        File.WriteAllText(mine, World("MyWorld", 1));
        File.WriteAllText(other, World("Other", 1));
        var zip = WorldBackups.List(_backups, SlotA, "MyWorld.json").Single();

        WorldBackups.Restore(zip, mine, _backups, SlotA, keep: 10);

        Assert.Equal(World("MyWorld", 0), File.ReadAllText(mine));
        Assert.Equal(World("Other", 1), File.ReadAllText(other));
        Assert.True(File.GetLastWriteTime(mine) >= DateTime.Now.AddMinutes(-1));
    }

    [Fact]
    public void Restore_ABackupThatIsntAWorld_ChangesNothing()
    {
        var world = WriteWorld("MyWorld", 1);
        Directory.CreateDirectory(_backups);
        var bad = Path.Combine(_backups, $"{SlotA}_world_before_settings_20261027-101010__MyWorld.json");
        File.WriteAllText(bad, "{\"NotAWorld\": true}");
        var backup = WorldBackups.List(_backups, SlotA, "MyWorld.json").Single();

        Assert.Throws<InvalidDataException>(() => WorldBackups.Restore(backup, world, _backups, SlotA, keep: 10));

        Assert.Equal(World("MyWorld", 1), File.ReadAllText(world));
        Assert.Single(WorldBackups.List(_backups, SlotA, "MyWorld.json"));
        Assert.Equal(["MyWorld.json"], Directory.GetFiles(_prospects).Select(Path.GetFileName));
    }

    [Fact]
    public void Restore_AnotherWorldsBackup_IsRefused()
    {
        var mine = WriteWorld("MyWorld", 1);
        var other = WriteWorld("Other", 0);
        WorldBackups.BackUp(other, _backups, SlotA, WorldBackupKind.BeforeSettingsSave);
        var otherBackup = WorldBackups.List(_backups, SlotA, "Other.json").Single();

        Assert.Throws<InvalidDataException>(() => WorldBackups.Restore(otherBackup, mine, _backups, SlotA, keep: 10));
        Assert.Equal(World("MyWorld", 1), File.ReadAllText(mine));
    }

    [Fact]
    public void Prune_KeepsTheOldestAndTheNewest_PerWorldAndSlot()
    {
        var world = WriteWorld("MyWorld", 0);
        var other = WriteWorld("Other", 0);
        var made = Enumerable.Range(0, 6).Select(_ => WorldBackups.BackUp(world, _backups, SlotA, WorldBackupKind.BeforeSettingsSave)).ToList();
        var otherWorlds = Enumerable.Range(0, 4).Select(_ => WorldBackups.BackUp(other, _backups, SlotA, WorldBackupKind.BeforeSettingsSave)).ToList();
        var otherSlot = Enumerable.Range(0, 4).Select(_ => WorldBackups.BackUp(world, _backups, SlotB, WorldBackupKind.BeforeSettingsSave)).ToList();
        WriteWorld("MyWorld", 0);
        var zip = FinderZip(SlotA, "20200101-000000");

        WorldBackups.Prune(_backups, SlotA, "MyWorld.json", keep: 3);

        var left = WorldBackups.List(_backups, SlotA, "MyWorld.json").Where(b => !b.IsZip).Select(b => b.Path).Order().ToList();
        Assert.Equal(new[] { made[0], made[4], made[5] }.Order(), left);
        Assert.All(otherWorlds.Concat(otherSlot).Append(zip), p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void WorldSave_Save_NamesItsBackupForTheSlot_AndPrunesToTheSetting()
    {
        var world = WriteWorld("MyWorld", 0);
        string? first = null;
        for (var i = 0; i < 5; i++)
        {
            var backup = WorldSave.Load(world)!.Save(new Dictionary<string, int> { ["Arachnophobia"] = i % 2 }, _backups, SlotA, backupsKept: 2);
            first ??= backup;
            Assert.StartsWith($"{SlotA}_world_before_settings_", Path.GetFileName(backup));
        }

        var left = WorldBackups.List(_backups, SlotA, "MyWorld.json");
        Assert.Equal(2, left.Count);
        Assert.Contains(left, b => b.Path == first);
    }
}
