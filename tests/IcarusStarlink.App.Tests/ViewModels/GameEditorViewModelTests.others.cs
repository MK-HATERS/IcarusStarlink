using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>What the other turned-on mods do to an item: a mark in the list, a line on the card, and a question at Save when your value would lose.</summary>
public sealed partial class GameEditorViewModelTests
{
    /// <summary>Merge &amp; Install's queue as the Editor reads it, built from real Library mods in this order.</summary>
    private sealed class FakeQueue(GameEditorViewModelTests owner) : IMergeQueueReader
    {
        public List<string> Folders { get; } = [];

        public double? Stacks { get; set; }

        public Dictionary<string, string> Winners { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Moves { get; private set; }

        public Task<MergeQueueState> ReadQueueAsync(CancellationToken cancellationToken = default)
        {
            var classifier = new DefaultSemanticClassifier();
            var entries = owner._library.GetAll().ToDictionary(e => e.FolderName, StringComparer.OrdinalIgnoreCase);
            var changes = Folders.Select(f => StatEntries.Split(ExmodFieldChangeMapper.ToFieldChanges(Read(owner._library, f), classifier))).ToList();
            var baseTables = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
            {
                ["Traits-D_Itemable.json"] = JsonNode.Parse("""{"Item_Envirosuit": {"Weight": 100}, "Item_Stone": {"Weight": 10}}""")!.AsObject(),
            };
            return Task.FromResult(new MergeQueueState([.. Folders], [.. Folders.Select(f => entries[f].Name)], changes, baseTables, Winners, Stacks));
        }

        public bool MoveToEnd(string folderName)
        {
            Moves++;
            Folders.Remove(folderName);
            Folders.Add(folderName);
            return true;
        }
    }

    private string ImportWeightMod(string name, int weight)
    {
        var source = Path.Combine(_root, "_" + name);
        ExmodFolder.Write(source, new ExmodPackageContents(
            new ExmodPackage
            {
                Name = name, Author = "Someone", Version = "1", Description = "", FileName = name.Replace(' ', '_'),
                Rows = [new ExmodFileRow { CurrentFile = "Traits-D_Itemable.json", FileItems = [new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["Weight"] = weight } }] }],
            }, []));
        return _library.Import(source, source: "Nexus", nexusModId: 7).FolderName;
    }

    /// <summary>Your mod first in the merge list, another one later setting the same weight.</summary>
    private (GameEditorViewModel Vm, FakeQueue Queue, string Mine) OpenWithOtherMod()
    {
        var mine = _library.CreateBlankMod("Mine", "Me").FolderName;
        var big = ImportWeightMod("Heavy Suits", 200);
        var queue = new FakeQueue(this);
        queue.Folders.AddRange([mine, big]);
        var vm = new GameEditorViewModel(_library, DataFolder, queueReader: queue);
        vm.EnsureLoaded();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine);
        return (vm, queue, mine);
    }

    private static GameItemSectionViewModel Itemable(GameEditorViewModel vm) => vm.Sections.Single(s => s.Section.Key == "Itemable");

    [Fact]
    public async Task OtherTurnedOnMods_MarkTheItem_AndTheCardSaysWhoseValueTheMergeKeeps()
    {
        var (vm, _, _) = OpenWithOtherMod();

        await vm.RefreshOtherModsAsync();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");

        Assert.True(vm.FilteredItems.Single(i => i.Name == "Envirosuit").IsChangedByOtherMods);
        Assert.False(vm.FilteredItems.Single(i => i.Name == "Stone").IsChangedByOtherMods);
        Assert.Equal("Your other turned-on mods change 1 item here (marked ◆).", vm.OtherModsText);
        var line = Assert.Single(Itemable(vm).OtherModLines);
        Assert.Equal("Weight: game 100 · with your turned-on mods 200, from Heavy Suits.", line);
        Assert.True(Itemable(vm).HasOtherModLines);
    }

    [Fact]
    public async Task Save_WhenAnotherModWouldWin_AsksFirst_AndCancelWritesNothing()
    {
        var (vm, queue, mine) = OpenWithOtherMod();
        await vm.RefreshOtherModsAsync();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";
        string? asked = null;
        vm.AskWhenValueLoses = (message, canMove) =>
        {
            asked = message;
            Assert.True(canMove);
            return null;
        };

        vm.SaveCommand.Execute(null);

        Assert.NotNull(asked);
        Assert.Contains("Xigo Envirosuit · Weight: Heavy Suits sets 200 (later in the merge list)", asked);
        Assert.Contains("Move my mod last", asked);
        Assert.Empty(Read(_library, mine).Rows.Where(r => r.CurrentFile == "Traits-D_Itemable.json"));
        Assert.Equal(1, vm.PendingCount); // still typed
        Assert.Equal(0, queue.Moves);
    }

    [Fact]
    public async Task Save_MoveMyModLast_SavesMovesItAndThenItWins()
    {
        var (vm, queue, mine) = OpenWithOtherMod();
        await vm.RefreshOtherModsAsync();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";
        var asks = 0;
        vm.AskWhenValueLoses = (_, _) =>
        {
            asks++;
            return true;
        };

        vm.SaveCommand.Execute(null);
        await vm.WaitForOtherModsAsync();

        Assert.Equal(1, asks);
        Assert.Equal(1, queue.Moves);
        Assert.Equal(mine, queue.Folders[^1]);
        Assert.Contains("'Mine' is now last in the merge list", vm.StatusMessage);
        var item = Read(_library, mine).Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single();
        Assert.Equal(50, item.Fields["Weight"]!.GetValue<int>());
        Assert.Equal("Weight: game 100 · with your turned-on mods 50, from this mod (it wins over Heavy Suits).", Assert.Single(Itemable(vm).OtherModLines));

        // Now last: changing it again asks nothing.
        Weight(vm).ValueText = "60";
        vm.SaveCommand.Execute(null);
        Assert.Equal(1, asks);
        Assert.StartsWith("Saved 1 change(s) to 'Mine'", vm.StatusMessage);
    }

    [Fact]
    public async Task Save_SaveAnyway_SavesWithoutMovingTheMod()
    {
        var (vm, queue, mine) = OpenWithOtherMod();
        await vm.RefreshOtherModsAsync();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.AskWhenValueLoses = (_, _) => false;

        vm.SaveCommand.Execute(null);
        await vm.WaitForOtherModsAsync();

        Assert.Equal(0, queue.Moves);
        Assert.NotEmpty(Read(_library, mine).Rows.Where(r => r.CurrentFile == "Traits-D_Itemable.json"));
        Assert.Equal("Weight: game 100 · with your turned-on mods 200, from Heavy Suits. This mod's value loses.", Assert.Single(Itemable(vm).OtherModLines));
    }

    /// <summary>A saved winner in Check the merge decides it: moving the mod wouldn't help, so that isn't offered.</summary>
    [Fact]
    public async Task Save_WhenAPickKeepsTheOtherMod_DoesNotOfferToMove()
    {
        var (vm, queue, _) = OpenWithOtherMod();
        queue.Winners[MergeConflictsViewModel.WinnerKey("Traits-D_Itemable.json", "Item_Envirosuit", "Weight")] = queue.Folders[1];
        queue.Folders.Reverse(); // your mod last, but the pick keeps the other one
        await vm.RefreshOtherModsAsync();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";
        bool? offeredMove = null;
        vm.AskWhenValueLoses = (message, canMove) =>
        {
            offeredMove = canMove;
            Assert.Contains("(your pick in Check the merge)", message);
            return false;
        };

        vm.SaveCommand.Execute(null);

        Assert.False(offeredMove);
    }

    /// <summary>The Stacks multiplier is applied after every mod: the card says so on its own.</summary>
    [Fact]
    public async Task StacksMultiplier_IsShownOnTheStackSize()
    {
        var (vm, queue, _) = OpenWithOtherMod();
        queue.Stacks = 4;
        await vm.RefreshOtherModsAsync();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");

        var stack = Itemable(vm).Fields.Single(f => f.Field.Path.Top == "MaxStack");
        Assert.Contains($"{stack.Label}: Merge & Install's Stacks ×4 option multiplies it when you build.", Itemable(vm).OtherModLines);
    }

    /// <summary>A mod not turned on loses to nothing: no question.</summary>
    [Fact]
    public async Task Save_IntoAModNotTurnedOn_AsksNothing()
    {
        var (vm, queue, mine) = OpenWithOtherMod();
        queue.Folders.Remove(mine);
        await vm.RefreshOtherModsAsync();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.AskWhenValueLoses = (_, _) => throw new InvalidOperationException("not asked");

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved 1 change(s) to 'Mine'", vm.StatusMessage);
    }
}
