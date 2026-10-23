using System.Text.Json.Nodes;
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

    /// <summary>Plan step 4: a mount's data is only ever edited when it reads and writes back exactly.</summary>
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

    /// <summary>A RecorderBlob of tagged properties: the mount's own name and its experience, then None and a 0.</summary>
    private static JsonArray Blob(string name, int experience)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        void FString(string s)
        {
            writer.Write(s.Length + 1);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(s));
            writer.Write((byte)0);
        }

        FString("MountName");
        FString("StrProperty");
        writer.Write(4 + name.Length + 1);
        writer.Write(0);
        writer.Write((byte)0);
        FString(name);

        FString("Experience");
        FString("IntProperty");
        writer.Write(4);
        writer.Write(0);
        writer.Write((byte)0);
        writer.Write(experience);

        FString("None");
        writer.Write(0);
        writer.Flush();
        return new JsonArray([.. stream.ToArray().Select(b => (JsonNode)JsonValue.Create((int)b))]);
    }

    private static IcarusStarlink.PakIO.Assets.MountGrowth GrowthOf100XpALevel()
    {
        var xp = new IcarusStarlink.PakIO.Assets.GrowthCurve(
            [new(0, 0, false, false, 0, 0), new(50, 5000, false, false, 0, 0)], LinearBefore: false, LinearAfter: false);
        return new IcarusStarlink.PakIO.Assets.MountGrowth("AI_Mounts", null, 3,
            new IcarusStarlink.PakIO.Assets.PlayerGrowth(50, 50, false, xp, null, null, null, []));
    }

    private static SaveMountViewModel MakeWithBlob(int level, int experience, Action? onDirtyChanged = null)
    {
        var node = new JsonObject
        {
            ["MountName"] = "Rex",
            ["MountLevel"] = level,
            ["MountType"] = "Moa",
            ["RecorderBlob"] = new JsonObject { ["BinaryData"] = Blob("Rex", experience) },
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
}
