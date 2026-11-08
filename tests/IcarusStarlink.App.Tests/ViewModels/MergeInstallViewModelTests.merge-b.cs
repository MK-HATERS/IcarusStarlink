using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Find the broken mod's side of Merge &amp; Install: putting the player's mods back where they were.</summary>
public sealed partial class MergeInstallViewModelTests
{
    /// <summary>
    /// Regression: a mod that left the queue during a search came back at the end of the queue, so
    /// it won every value it shares with the mods after it (last in the queue wins by default) and
    /// the restored install differed from the one before the search.
    /// </summary>
    [Fact]
    public async Task FindBrokenPutBack_AModTakenOutMeanwhile_GoesBackInItsOwnPlace()
    {
        var (vm, _, _, _, _) = FinderSetup();
        vm.RemoveFromQueueByFolderNames(["ModB"]);

        var failed = await vm.InstallWholeQueueForFinderAsync(["ModA", "ModB", "ModC"]);

        Assert.Null(failed);
        Assert.Equal(["ModA", "ModB", "ModC"], vm.Queue.Select(e => e.FolderName));
    }

    /// <summary>The first of two missing mods goes before the second, which goes before the next mod still queued.</summary>
    [Fact]
    public async Task FindBrokenPutBack_TwoModsTakenOut_BothGoBackInOrder()
    {
        var (vm, _, _, _, _) = FinderSetup();
        vm.RemoveFromQueueByFolderNames(["ModA", "ModB"]);

        Assert.Null(await vm.InstallWholeQueueForFinderAsync(["ModA", "ModB", "ModC"]));

        Assert.Equal(["ModA", "ModB", "ModC"], vm.Queue.Select(e => e.FolderName));
    }

    /// <summary>The queue refuses changes while a build runs: the finder hears that, so it doesn't record the mod as off.</summary>
    [Fact]
    public void FinderTurnOff_WhileMergeAndInstallBuilds_IsRefused()
    {
        var (vm, _, _, _, _) = FinderSetup();
        var host = new MergeInstallFinderHost(() => vm, null!, new FakeGameProcessChecker(false), Path.Combine(_tempRoot, "Worlds"));
        vm.IsRebuilding = true;

        Assert.False(host.RemoveFromQueue("ModB"));
        Assert.Contains("ModB", vm.Queue.Select(e => e.FolderName));

        vm.IsRebuilding = false;
        Assert.True(host.RemoveFromQueue("ModB"));
        Assert.DoesNotContain("ModB", vm.Queue.Select(e => e.FolderName));
    }
}
