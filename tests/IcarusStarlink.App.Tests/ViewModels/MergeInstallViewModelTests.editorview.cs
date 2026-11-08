using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The queue as the Editor page reads it, and its "Move my mod last".</summary>
public sealed partial class MergeInstallViewModelTests
{
    [Fact]
    public async Task ReadQueue_GivesTheMergeOrderAndTheirChanges_AndMoveToEndPutsAModLast()
    {
        var libraryRepository = new FakeLibraryRepository();
        libraryRepository.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        libraryRepository.FolderPaths["ModB"] = CreateExmodFolder("ModB", BuildCraftTimeExmodJson("ModB", craftTime: 99), out _);
        var rebuildService = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        var vm = CreateViewModel(libraryRepository: libraryRepository, rebuildService: rebuildService);
        vm.Queue.Add(MakeEntry("ModA"));
        vm.Queue.Add(MakeEntry("ModB"));

        var state = await vm.ReadQueueAsync();

        Assert.Equal(["ModA", "ModB"], state.Ids);
        Assert.Equal(2, state.OrderedChanges.Count);
        Assert.Contains(state.OrderedChanges[1], c => c.ItemName == "SmelterRecipe" && c.FieldName == "CraftTime" && c.NewValue!.GetValue<int>() == 99);
        Assert.NotNull(state.BaseTables);

        Assert.True(vm.MoveToEnd("ModA"));
        Assert.Equal(["ModB", "ModA"], vm.Queue.Select(e => e.FolderName));
        Assert.False(vm.MoveToEnd("NotQueued"));
    }
}
