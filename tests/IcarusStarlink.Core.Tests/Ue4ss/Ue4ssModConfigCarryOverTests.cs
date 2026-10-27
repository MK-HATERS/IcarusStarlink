using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

/// <summary>
/// An update of a UE4SS mod keeps the player's settings (user report 2026-10-27: a new Nexus
/// version replaced config.lua with the shipped one), on the new version's layout: new settings
/// at their defaults, settings the version no longer has left out and named with the old values.
/// </summary>
public sealed class Ue4ssModConfigCarryOverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ConfigCarryOver_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Mod(string side, params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(_root, side, "MkHatersTools");
        Directory.CreateDirectory(folder);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return folder;
    }

    [Fact]
    public void KeepInstalledConfigs_PlayersValuesGoIntoTheNewLayout_NewAtDefaults_GoneOnesNamed()
    {
        const string mine = "return {\n    MaxScale = 2.0, -- mine\n    OldName = 7,\n    Enabled = true,\n}\n";
        const string shipped = "-- v2\nreturn {\n    Enabled = true, -- the mod [restart]\n    MaxScale = 4.0, -- biggest\n    MinScale = 0.25, -- new\n}\n";
        const string shippedDefaults = "return {\n    Enabled = true,\n    MaxScale = 4.0,\n    MinScale = 0.25,\n}\n";
        var previous = Mod("previous", ("config.lua", mine), ("config.default.lua", "return {\n    MaxScale = 4.0,\n}\n"), ("Scripts/main.lua", "old"));
        var updated = Mod("new", ("config.lua", shipped), ("config.default.lua", shippedDefaults), ("Scripts/main.lua", "new"));

        var result = Ue4ssModConfigCarryOver.KeepInstalledConfigs(previous, updated, "2.0");

        Assert.Equal(["config.lua"], result.Kept);
        Assert.Equal("-- v2\nreturn {\n    Enabled = true, -- the mod [restart]\n    MaxScale = 2.0, -- biggest\n    MinScale = 0.25, -- new\n}\n", File.ReadAllText(Path.Combine(updated, "config.lua")));
        Assert.Equal(shippedDefaults, File.ReadAllText(Path.Combine(updated, "config.default.lua")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(updated, "Scripts", "main.lua")));
        var review = Assert.Single(result.Reviews);
        Assert.Equal(("config.lua", "2.0", true), (review.File, review.Version, review.MergedIntoNewLayout));
        Assert.Equal(["MinScale"], review.Added);
        Assert.Equal(["OldName = 7"], review.Removed);
        Assert.Equal(["MaxScale"], review.Kept);
        Assert.Equal(["config.lua: MinScale"], result.NewSettings);
        Assert.Equal(["config.lua: OldName = 7"], result.RemovedSettings);
    }

    /// <summary>A file the merge can't carry (Wildlife's Herds over several lines) is kept as it was; the new settings are only named.</summary>
    [Fact]
    public void KeepInstalledConfigs_AFileWithATableOverSeveralLines_IsKeptAsItIs()
    {
        const string mine = "return {\n    Radius = 9,\n    Herds = {\n        { young = \"BearCub\", adults = { \"Bear\" } },\n    },\n}\n";
        var previous = Mod("previous", ("config.lua", mine));
        var updated = Mod("new", ("config.lua", "return {\n    Radius = 5,\n    Herds = {\n        { young = \"BearCub\", adults = { \"Bear\" } },\n    },\n    Debug = false,\n}\n"));

        var result = Ue4ssModConfigCarryOver.KeepInstalledConfigs(previous, updated);

        Assert.Equal(mine, File.ReadAllText(Path.Combine(updated, "config.lua")));
        var review = Assert.Single(result.Reviews);
        Assert.False(review.MergedIntoNewLayout);
        Assert.Equal(["Debug"], review.Added);
        Assert.Empty(review.Removed);
    }

    /// <summary>The shipped config is used only where the previous copy had none; nothing to review then.</summary>
    [Fact]
    public void KeepInstalledConfigs_ShippedConfigStays_WhenThePreviousCopyHadNone()
    {
        var previous = Mod("previous", ("Scripts/main.lua", "old"));
        var updated = Mod("new", ("config.lua", "shipped"), ("Scripts/main.lua", "new"));

        var result = Ue4ssModConfigCarryOver.KeepInstalledConfigs(previous, updated);

        Assert.Empty(result.Kept);
        Assert.Empty(result.Reviews);
        Assert.Equal("shipped", File.ReadAllText(Path.Combine(updated, "config.lua")));
    }

    [Fact]
    public void KeepInstalledConfigs_IniInTheConfigFolder_MergesTheSameWay_AndAConfigTheNewVersionLacksComesAlong()
    {
        var previous = Mod("previous", (Path.Combine("config", "Pets.ini"), "[A]\nX = 2\nOld = 1\n"), ("settings.lua", "return { old = true }"));
        var updated = Mod("new", (Path.Combine("config", "Pets.ini"), "[A]\nX = 1 ; how many\nY = 0\n"));

        var result = Ue4ssModConfigCarryOver.KeepInstalledConfigs(previous, updated);

        Assert.Equal(["settings.lua", Path.Combine("config", "Pets.ini")], result.Kept); // the mod folder's own file first
        Assert.Equal("[A]\nX = 2 ; how many\nY = 0\n", File.ReadAllText(Path.Combine(updated, "config", "Pets.ini")));
        Assert.Equal("return { old = true }", File.ReadAllText(Path.Combine(updated, "settings.lua")));
        var review = Assert.Single(result.Reviews);
        Assert.Equal(["[A] Y"], review.Added);
        Assert.Equal(["[A] Old = 1"], review.Removed);
        Assert.Equal(["[A] X"], review.Kept);
    }

    /// <summary>ApplyValues (a shared patch's settings): the local layout takes the values; applied again with nothing new it writes nothing and makes no backup (review 2026-10-27: a patch applied twice piled up identical backups).</summary>
    [Fact]
    public void ApplyValues_WritesTheValuesIntoTheLocalLayout_AndIsQuietTheSecondTime()
    {
        var mod = Mod("mine", ("config.lua", "return {\n    Range = 10, -- how far\n}\n"));
        var backups = Path.Combine(_root, "backups");

        var removed = Ue4ssModConfigCarryOver.ApplyValues(mod, "config.lua", "return {\n    Range = 50,\n    Gone = 1,\n}\r\n", backups);

        Assert.Equal("return {\n    Range = 50, -- how far\n}\n", File.ReadAllText(Path.Combine(mod, "config.lua")));
        Assert.Equal(["Gone = 1"], removed);
        Assert.Single(Directory.GetFiles(backups, "config.lua", SearchOption.AllDirectories));

        Ue4ssModConfigCarryOver.ApplyValues(mod, "config.lua", "return {\n    Range = 50,\n    Gone = 1,\n}\r\n", backups);

        Assert.Single(Directory.GetFiles(backups, "config.lua", SearchOption.AllDirectories));
    }

    /// <summary>The same settings in both: nothing to review, and the file is the new layout with nothing changed.</summary>
    [Fact]
    public void KeepInstalledConfigs_NothingChanged_NothingToReview()
    {
        var previous = Mod("previous", ("config.lua", "return {\n    A = 1,\n}\n"));
        var updated = Mod("new", ("config.lua", "return {\n    A = 1, -- newer comment\n}\n"));

        var result = Ue4ssModConfigCarryOver.KeepInstalledConfigs(previous, updated);

        Assert.Empty(result.Reviews);
        Assert.Equal("return {\n    A = 1, -- newer comment\n}\n", File.ReadAllText(Path.Combine(updated, "config.lua")));
    }
}
