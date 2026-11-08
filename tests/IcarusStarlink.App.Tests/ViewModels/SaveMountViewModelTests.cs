using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class SaveMountViewModelTests
{
    private static (SaveMountViewModel ViewModel, JsonObject Node) Make(int initialLevel = 5)
    {
        var node = new JsonObject { ["MountName"] = "Rex", ["MountLevel"] = initialLevel, ["MountType"] = "Moa" };
        var vm = new SaveMountViewModel(node, "Rex", initialLevel, "Moa", iconPath: null, onDirtyChanged: () => { });
        return (vm, node);
    }

    /// <summary>
    /// Regression guard: MarkClean() used to adopt a negative LevelText as the new "clean" baseline
    /// even though ApplyToNode() (Save's actual write) rejects a negative level and leaves
    /// MountLevel unchanged — so after a Save, IsDirty compared LevelText against itself and read
    /// false, showing "Saved" with the on-disk MountLevel still the original, unwritten one.
    /// </summary>
    [Fact]
    public void MarkClean_NegativeLevelText_NotAdoptedAsBaseline_StaysDirty()
    {
        var (vm, node) = Make(initialLevel: 5);
        vm.LevelText = "-3";

        vm.ApplyToNode();
        vm.MarkClean();

        Assert.Equal(5, node["MountLevel"]!.GetValue<int>());
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void MarkClean_ValidLevelText_AdoptedAsBaseline_BecomesClean()
    {
        var (vm, node) = Make(initialLevel: 5);
        vm.LevelText = "10";

        vm.ApplyToNode();
        vm.MarkClean();

        Assert.Equal(10, node["MountLevel"]!.GetValue<int>());
        Assert.False(vm.IsDirty);
    }

    /// <summary>A mount's data is only ever edited when it reads and writes back exactly.</summary>
    [Fact]
    public void Blob_ThatCantBeRead_IsRefused()
    {
        Assert.Null(IcarusStarlink.App.Services.MountBlob.TryRead([1, 2, 3, 4, 5], out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Mount_WhoseDataCantBeRead_StaysLabelOnly_AndItsDataIsNeverWritten()
    {
        var garbage = new JsonArray(1, 2, 3, 4, 5);
        var node = new JsonObject
        {
            ["MountName"] = "Frank",
            ["MountLevel"] = 7,
            ["MountType"] = "Dog",
            ["RecorderBlob"] = new JsonObject { ["ComponentClassName"] = "x", ["BinaryData"] = garbage },
        };
        var vm = new SaveMountViewModel(node, "Frank", 7, "Dog", null, () => { });

        Assert.False(vm.CanEditInside);
        Assert.NotNull(vm.InsideRefusal);

        vm.Name = "Rex";
        vm.LevelText = "9";
        vm.ApplyToNode();

        Assert.Equal("Rex", (string?)node["MountName"]);
        Assert.Equal(9, (int?)node["MountLevel"]);
        Assert.Same(garbage, node["RecorderBlob"]!["BinaryData"]);
    }

    private static IcarusStarlink.PakIO.Assets.MountGrowth GrowthOf100XpALevel(string? archetype = null) => GrowthWithXp(5000, archetype);

    /// <summary>Levels 0 to 50, XP rising evenly to <paramref name="xpAt50"/>.</summary>
    private static IcarusStarlink.PakIO.Assets.MountGrowth GrowthWithXp(float xpAt50, string? archetype = null)
    {
        var xp = new IcarusStarlink.PakIO.Assets.GrowthCurve(
            [new(0, 0, false, false, 0, 0), new(50, xpAt50, false, false, 0, 0)], LinearBefore: false, LinearAfter: false);
        return new IcarusStarlink.PakIO.Assets.MountGrowth("AI_Mounts", archetype, 3,
            new IcarusStarlink.PakIO.Assets.PlayerGrowth(50, 50, false, xp, null, null, null, []));
    }

    private static SaveMountViewModel MakeWithBlob(int level, int experience, Action? onDirtyChanged = null) =>
        MakeWithData(level, MountBlobFixture.Blob("Rex", experience), onDirtyChanged);

    private static SaveMountViewModel MakeWithData(int level, JsonArray data, Action? onDirtyChanged = null)
    {
        var node = new JsonObject
        {
            ["MountName"] = "Rex",
            ["MountLevel"] = level,
            ["MountType"] = "Moa",
            ["RecorderBlob"] = new JsonObject { ["BinaryData"] = data },
        };
        return new SaveMountViewModel(node, "Rex", level, "Moa", null, onDirtyChanged ?? (() => { }));
    }

    /// <summary>
    /// Level 10, Save, then 12 and back to 10 used to put back the experience from before the
    /// first Save (MarkClean never took the saved one), without the mount even looking changed.
    /// </summary>
    [Fact]
    public void LevelSetBackAfterASave_GetsTheSavedExperience_AndIsClean()
    {
        var vm = MakeWithBlob(level: 5, experience: 523);
        Assert.True(vm.CanEditInside, vm.InsideRefusal);
        vm.Growth = GrowthOf100XpALevel();

        vm.LevelText = "10";
        Assert.Equal(1000, vm.Blob!.Experience);
        vm.ApplyToNode();
        vm.MarkClean();

        vm.LevelText = "12";
        Assert.Equal(1200, vm.Blob.Experience);
        Assert.True(vm.IsDirty);
        vm.LevelText = "10";

        Assert.Equal(1000, vm.Blob.Experience);
        Assert.False(vm.IsDirty);
    }

    /// <summary>The row's "changed" dot binds IsDirty: it has to be raised, and an edit set back clears it.</summary>
    [Fact]
    public void Edits_RaiseIsDirty_AndAnInsideEditSetBack_IsClean()
    {
        var pageCalls = 0;
        var vm = MakeWithBlob(level: 5, experience: 523, () => pageCalls++);
        vm.Growth = GrowthOf100XpALevel();
        var raised = 0;
        vm.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(SaveMountViewModel.IsDirty) ? 1 : 0;

        vm.Name = "Rexy";
        Assert.True(vm.IsDirty);
        vm.Name = "Rex";
        vm.LevelText = "7";
        Assert.True(vm.IsDirty);
        vm.LevelText = "5";

        Assert.False(vm.IsDirty);
        Assert.Equal(523, vm.Blob!.Experience);
        Assert.True(raised >= 4);
        Assert.True(pageCalls >= 4);
    }

    /// <summary>
    /// Regression: a blob value outside 0-255 (300, -1) was cast to a byte and wrapped (300 to 44),
    /// the write-back check then compared the wrapped bytes with themselves and passed, and a later
    /// Save wrote bytes the save never had.
    /// </summary>
    [Fact]
    public void BlobByteOutOfRange_IsRefused()
    {
        var data = MountBlobFixture.Blob("Rex", 523);
        data[0] = data[0]!.GetValue<int>() + 256;

        var vm = MakeWithData(5, data);

        Assert.False(vm.CanEditInside);
        Assert.Contains("byte", vm.InsideRefusal);
    }

    [Fact]
    public void BlobFarBiggerThanAMounts_IsRefusedWithoutReadingIt()
    {
        var data = MountBlobFixture.Blob("Rex", 523);
        for (var i = data.Count; i <= 1_048_576; i++)
        {
            data.Add(0);
        }

        var vm = MakeWithData(5, data);

        Assert.False(vm.CanEditInside);
        Assert.Contains("far bigger", vm.InsideRefusal);
    }

    /// <summary>
    /// Regression: a mount whose saved data has no talent entries can't be given a talent (a new
    /// one is a copy of an existing entry), yet its + stayed enabled and every click silently did nothing.
    /// </summary>
    [Fact]
    public void MountWithNoTalentEntries_SaysSo_AndOffersNoRankUp()
    {
        var data = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_MountTalents", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "Talents"));
        try
        {
            File.WriteAllText(Path.Combine(data, "Talents", "D_TalentArchetypes.json"), """{ "Rows": [ { "Name": "Creature_Wolf" } ] }""");
            File.WriteAllText(Path.Combine(data, "Talents", "D_TalentTrees.json"), """
                { "Rows": [ { "Name": "Wolf_Tree", "Archetype": { "RowName": "Creature_Wolf" } } ] }
                """);
            File.WriteAllText(Path.Combine(data, "Talents", "D_Talents.json"), """
                { "Rows": [ { "Name": "Wolf_Health", "TalentTree": { "RowName": "Wolf_Tree" }, "Rewards": [ {}, {} ] } ] }
                """);
            var vm = MakeWithBlob(5, 523);

            vm.SetGameData(new SaveGameNames(data), GrowthOf100XpALevel("Creature_Wolf"));

            var talent = Assert.Single(vm.TalentRows);
            Assert.False(talent.CanRankUp);
            Assert.NotNull(vm.TalentsNote);
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    /// <summary>
    /// Regression: with no growth table read, a new level was written to the list while the
    /// mount's experience stayed as it was, so the game puts the level back, and nothing said so.
    /// </summary>
    [Fact]
    public void NewLevelWithoutAGrowthCurve_SaysTheGameMayPutItBack()
    {
        var vm = MakeWithBlob(5, 523);

        vm.LevelText = "20";
        Assert.NotNull(vm.LevelNote);
        Assert.Equal(523, vm.Blob!.Experience);

        vm.LevelText = "5";
        Assert.Null(vm.LevelNote);

        vm.LevelText = "20";
        vm.Growth = GrowthOf100XpALevel();
        Assert.Null(vm.LevelNote);
        Assert.Equal(2000, vm.Blob.Experience);
    }

    /// <summary>Regression: a level whose experience is past what the save's number holds wrote int.MaxValue (or a wrapped value) into the blob.</summary>
    [Fact]
    public void LevelWhoseExperienceDoesntFit_LeavesTheExperience_AndSaysSo()
    {
        var vm = MakeWithBlob(5, 523);
        vm.Growth = GrowthWithXp(5e10f);

        vm.LevelText = "20";

        Assert.Equal(523, vm.Blob!.Experience);
        Assert.NotNull(vm.LevelNote);
    }
}
