using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Editor page: change a game item, save into a new mod or one of your own — never a downloaded one.</summary>
[Collection(MessengerCollection.Name)]
public sealed partial class GameEditorViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "GameEditor_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public GameEditorViewModelTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"},"Decayable":{"RowName":"None","DataTableName":"D_Decayable"},"Armour":{"RowName":"None","DataTableName":"D_Armour"}},"Rows":[
              {"Name":"Envirosuit","Itemable":{"RowName":"Item_Envirosuit"},"Decayable":{"RowName":"Decay_General"},"Armour":{"RowName":"IO_Undersuit"}},
              {"Name":"Stone","Itemable":{"RowName":"Item_Stone"},"Decayable":{"RowName":"Decay_General"}}
            ]}
            """);
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0,"MaxStack":1},"Rows":[
              {"Name":"Item_Envirosuit","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-DisplayName\", \"Xigo Envirosuit\")","Weight":100,
               "Icon":"/Game/Assets/2DArt/UI/Items/Item_Icons/Armour/ITEM_Envirosuit_1.ITEM_Envirosuit_1"},
              {"Name":"Item_Stone","Weight":10,"Icon":"/Game/Assets/2DArt/UI/Items/Item_Icons/Voxels/ITEM_Stone.ITEM_Stone",
               "Description":"NSLOCTEXT(\"D_Itemable\", \"Item_Stone-Description\", \"A hard rock.\")"}
            ]}
            """);
        Write("Traits/D_Armour.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"IO_Undersuit","ArmourStats":{"(Value=\"BaseOxygenSlots_+\")":1}}]}""");
        Write("Stats/D_Stats.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"BaseMovementSpeed_+"},{"Name":"BaseMovementSpeed_+%"},{"Name":"BaseColdResistance_%"},{"Name":"MovementSpeed_+"}]}""");
        Write("Traits/D_Decayable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Decay_General","DecayTime":300}]}""");
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string json)
    {
        var path = Path.Combine(DataFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private GameEditorViewModel Open()
    {
        var vm = new GameEditorViewModel(_library, DataFolder);
        vm.EnsureLoaded();
        return vm;
    }

    private string ImportNexusMod()
    {
        var source = Path.Combine(_root, "_nexus");
        ExmodFolder.Write(source, new ExmodPackageContents(
            new ExmodPackage { Name = "From Nexus", Author = "Someone", Version = "1", Description = "", FileName = "From_Nexus", Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }] }, []));
        return _library.Import(source, source: "Nexus", nexusModId: 42).FolderName;
    }

    private static ExmodPackage Read(FolderLibraryRepository library, string folder) => ExmodFolder.Read(library.GetFolderPath(folder)).Package;

    /// <summary>The empty state offers only common items the loaded game data has; one opens that item.</summary>
    [Fact]
    public void QuickStarts_AreOnlyItemsInTheGameData_AndOpenTheItem()
    {
        var vm = Open();

        var quickStart = Assert.Single(vm.QuickStarts);
        Assert.Equal("Envirosuit", quickStart.Item.Name);
        Assert.Equal("Xigo Envirosuit", quickStart.Label);
        Assert.True(vm.ShowPickPrompt);

        vm.OpenQuickStartCommand.Execute(quickStart);

        Assert.Equal("Envirosuit", vm.SelectedItem?.Name);
        Assert.True(vm.HasItem);
        Assert.False(vm.ShowPickPrompt);
    }

    /// <summary>The item list's two lines: the in-game name, then the row name (none when they're the same).</summary>
    [Fact]
    public void ItemList_ShowsTheInGameNameThenTheRowName()
    {
        var vm = Open();

        var suit = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Assert.Equal("Xigo Envirosuit", suit.Title);
        Assert.Equal("Envirosuit", suit.Subtitle);
        var stone = vm.FilteredItems.Single(i => i.Name == "Stone");
        Assert.Equal("Stone", stone.Title);
        Assert.Null(stone.Subtitle);
    }

    [Fact]
    public void SaveTargets_AreANewModAndOnlyModsMadeInStarlink()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        ImportNexusMod();

        var vm = Open();

        Assert.Equal(["A new mod", "Mine"], vm.Targets.Select(t => t.Display));
        Assert.True(_library.GetAll().Single(e => e.FolderName == mine.FolderName).MadeInStarlink);
    }

    /// <summary>"Only changed values" hides the game's own values (never drops them: Save still reads every card), and stays on for the next item.</summary>
    [Fact]
    public void OnlyChangedValues_HidesTheGamesValues_UntilSwitchedOff()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";

        vm.ShowChangedValuesOnly = true;

        var itemable = vm.Sections.Single(s => s.Section.Key == "Itemable");
        Assert.True(Weight(vm).IsShown);
        Assert.All(itemable.Fields.Where(f => f != Weight(vm)), f => Assert.False(f.IsShown));
        Assert.All(vm.Sections.Where(s => s != itemable && s.Fields.Count > 0), s => Assert.True(s.AllHidden));
        Assert.Single(vm.PendingGroups.SelectMany(g => g.Changes));

        // Put back to the game's value, it stays in view (no field vanishes under the cursor).
        Weight(vm).UseGameValueCommand.Execute(null);
        Assert.True(Weight(vm).IsShown);

        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Stone");
        Assert.All(vm.Sections.SelectMany(s => s.Fields), f => Assert.False(f.IsShown));

        vm.ShowChangedValuesOnly = false;
        Assert.All(vm.Sections.SelectMany(s => s.Fields), f => Assert.True(f.IsShown));
        Assert.All(vm.Sections, s => Assert.False(s.AllHidden));
    }

    /// <summary>"Your changes" lists each changed value, and its x puts that one back.</summary>
    [Fact]
    public void YourChanges_ListsEachChange_AndItsUndoPutsItBack()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        var weight = vm.Sections.Single(s => s.Section.Key == "Itemable").Fields.Single(f => f.Label == "Weight");
        var before = weight.ValueText;
        Assert.True(vm.HasNoPendingChanges);

        weight.ValueText = "50";
        var change = Assert.Single(vm.PendingGroups.SelectMany(g => g.Changes));
        Assert.Contains("Weight: 50", change.Text);
        Assert.False(vm.HasNoPendingChanges);

        change.Field.UndoChangeCommand.Execute(null);
        Assert.Equal(before, weight.ValueText);
        Assert.Empty(vm.PendingGroups.SelectMany(g => g.Changes));
        Assert.Equal(0, vm.PendingCount);
    }

    [Theory]
    [InlineData(10800, "3 h")]
    [InlineData(900, "15 min")]
    [InlineData(5400, "1 h 30 min")]
    [InlineData(90, "1 min 30 s")]
    [InlineData(90061, "1 d 1 h")]
    public void FieldDuration_ReadsSecondsAsPeopleSayThem(double seconds, string expected)
    {
        Assert.Equal(expected, GameItemFieldViewModel.Duration(seconds));
    }

    [Fact]
    public void Save_ToANewMod_CreatesItWithJustTheChange()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        vm.Sections.Single(s => s.Section.Key == "Itemable").Fields.Single(f => f.Label == "Weight").ValueText = "50";
        Assert.Equal(1, vm.PendingCount);
        vm.NewModName = "Lighter Suit";

        vm.SaveCommand.Execute(null);

        var entry = Assert.Single(_library.GetAll());
        Assert.Equal("Lighter Suit", entry.Name);
        Assert.True(entry.IsOwnMod);
        var item = Read(_library, entry.FolderName).Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single();
        Assert.Equal(["Weight"], item.Fields.Keys);
        Assert.Equal(50, item.Fields["Weight"]!.GetValue<int>());
        Assert.StartsWith("Saved 1 change(s) to 'Lighter Suit'", vm.StatusMessage);
        // Now it's the target: the page shows the mod's value, nothing pending.
        Assert.Equal(entry.FolderName, vm.SelectedTarget?.FolderName);
        Assert.Equal(0, vm.PendingCount);
        Assert.Equal("50", vm.Sections.Single(s => s.Section.Key == "Itemable").Fields.Single(f => f.Label == "Weight").ValueText);
    }

    /// <summary>The default is the safe one: a shared row becomes the item's own copy, so Stone's decay doesn't change.</summary>
    [Fact]
    public void Save_ASharedRow_DefaultsToTheItemsOwnCopy()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        var decay = vm.Sections.Single(s => s.Section.Key == "Decayable");
        Assert.True(decay.IsShared);
        Assert.True(decay.OwnCopy);
        decay.Fields.Single().ValueText = "600";

        vm.SaveCommand.Execute(null);

        var package = Read(_library, mine.FolderName);
        Assert.Equal("Decay_General_Envirosuit", package.Rows.Single(r => r.CurrentFile == "Traits-D_Decayable.json").FileItems.Single().Name);
        Assert.Contains(package.Rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
    }

    [Fact]
    public void Save_IntoAModFromNexus_IsRefused_EvenIfItWereSelected()
    {
        var nexus = ImportNexusMod();
        var vm = Open();
        vm.Targets.Add(new EditorSaveTarget(nexus, "From Nexus")); // a stale list, or a bug elsewhere
        vm.SelectedTarget = vm.Targets[^1];
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        vm.Sections.Single(s => s.Section.Key == "Itemable").Fields.Single(f => f.Label == "Weight").ValueText = "50";

        vm.SaveCommand.Execute(null);

        Assert.Contains("only mods you made in Starlink", vm.StatusMessage);
        Assert.DoesNotContain(Read(_library, nexus).Rows, r => r.CurrentFile == "Traits-D_Itemable.json");
    }

    [Fact]
    public void Searching_KeepsThePickedItemAndWhatWasTyped()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        vm.Sections.Single(s => s.Section.Key == "Itemable").Fields.Single(f => f.Label == "Weight").ValueText = "50";

        vm.FilterText = "stone";
        vm.FlushFilter();

        Assert.Equal("Envirosuit", vm.SelectedItem?.Name);
        Assert.Equal(1, vm.PendingCount);
    }

    [Fact]
    public void ANumberThatIsntOne_BlocksSaving_WithTheReason()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        vm.Sections.Single(s => s.Section.Key == "Itemable").Fields.Single(f => f.Label == "Weight").ValueText = "heavy";
        vm.NewModName = "X";

        vm.SaveCommand.Execute(null);

        Assert.Contains("Needs a number", vm.StatusMessage);
        Assert.Empty(_library.GetAll());
    }

    /// <summary>A real report: the stat list showed "movement speed" twice (it's a flat and a percent stat) and had no search.</summary>
    [Fact]
    public void AddingAStat_SearchesByPlainName_AndTellsFlatFromPercent()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        var armour = vm.Sections.Single(s => s.Section.Key == "Armour");

        armour.Picker.StatSearch = "movement";

        Assert.Equal(["Movement speed  ·  flat amount (10 = +10)", "Movement speed +%  ·  percent bonus (10 = +10%)"], armour.Picker.MatchingStats.Select(s => s.Display));
        armour.Picker.SelectedStat = armour.Picker.MatchingStats[1];
        Assert.StartsWith("Movement speed +%", armour.Picker.SelectedStatText);

        armour.Picker.AddStatCommand.Execute(null);

        var added = armour.Fields.Single(f => f.Label == "Movement speed +%");
        Assert.Equal("0", added.ValueText);
        Assert.Equal("", armour.Picker.StatSearch);
        Assert.Null(armour.Picker.SelectedStat);
        Assert.Equal(1, vm.PendingCount);
    }

    private static GameItemFieldViewModel Weight(GameEditorViewModel vm) => vm.Sections.Single(s => s.Section.Key == "Itemable").Fields.Single(f => f.Label == "Weight");

    private static EditorItemEntry Item(GameEditorViewModel vm, string name) => vm.FilteredItems.Single(i => i.Name == name);

    /// <summary>Clicking another item used to throw away (later: ask to throw away) what was typed; now each item keeps its changes until one Save writes them all.</summary>
    [Fact]
    public void SwitchingItems_KeepsEachItemsChanges_WithoutAsking()
    {
        var vm = Open();
        vm.ConfirmDiscard = _ => throw new InvalidOperationException("Switching items must not ask.");
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";

        vm.SelectedItem = Item(vm, "Stone");
        Assert.Equal("10", Weight(vm).ValueText);
        Weight(vm).ValueText = "5";

        Assert.Equal(2, vm.PendingCount);
        Assert.Equal(2, vm.EditedItemCount);
        Assert.Equal("YOUR CHANGES (2 IN 2 ITEMS)", vm.PendingHeaderText);
        Assert.Equal(["Stone", "Envirosuit"], vm.PendingGroups.Select(g => g.ItemName)); // the shown item first
        Assert.True(Item(vm, "Envirosuit").HasUnsavedChanges);

        vm.SelectedItem = Item(vm, "Envirosuit");
        Assert.Equal("50", Weight(vm).ValueText);
        Assert.Equal(2, vm.PendingCount);
    }

    [Fact]
    public void OneSave_WritesEveryItemsChanges()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.SelectedItem = Item(vm, "Stone");
        Weight(vm).ValueText = "5";
        vm.NewModName = "Lighter";

        vm.SaveCommand.Execute(null);

        var entry = Assert.Single(_library.GetAll());
        var rows = Read(_library, entry.FolderName).Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems;
        Assert.Equal(50, rows.Single(i => i.Name == "Item_Envirosuit").Fields["Weight"]!.GetValue<int>());
        Assert.Equal(5, rows.Single(i => i.Name == "Item_Stone").Fields["Weight"]!.GetValue<int>());
        Assert.StartsWith("Saved 2 change(s) to 'Lighter' (2 items)", vm.StatusMessage);
        Assert.Equal(0, vm.PendingCount);
        Assert.All(vm.FilteredItems, i => Assert.False(i.HasUnsavedChanges));
        Assert.All(vm.FilteredItems, i => Assert.True(i.IsInTargetMod));
    }

    /// <summary>The × beside an item in Your changes throws away only that item's changes; Discard throws away every item's.</summary>
    [Fact]
    public void DiscardingOneItem_KeepsTheOthers_AndDiscardThrowsAwayAll()
    {
        var vm = Open();
        vm.ConfirmDiscard = _ => true;
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.SelectedItem = Item(vm, "Stone");
        Weight(vm).ValueText = "5";

        vm.DiscardPendingItemCommand.Execute(vm.PendingGroups.Single(g => g.ItemName == "Envirosuit"));

        Assert.Equal(["Stone"], vm.PendingGroups.Select(g => g.ItemName));
        Assert.False(Item(vm, "Envirosuit").HasUnsavedChanges);

        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "60";
        vm.DiscardChangesCommand.Execute(null);

        Assert.Equal(0, vm.PendingCount);
        Assert.Equal("100", Weight(vm).ValueText);
    }

    /// <summary>Clicking an item's name in Your changes shows it, with what was typed for it.</summary>
    [Fact]
    public void AnItemInYourChanges_OpensWithItsChanges()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.SelectedItem = Item(vm, "Stone");
        vm.FilterText = "stone";
        vm.FlushFilter();

        vm.OpenPendingItemCommand.Execute(vm.PendingGroups.Single(g => g.ItemName == "Envirosuit"));

        Assert.Equal("Envirosuit", vm.SelectedItem?.Name);
        Assert.Contains(vm.SelectedItem, vm.FilteredItems);
        Assert.Equal("50", Weight(vm).ValueText);
    }

    /// <summary>A waiting item's changes come along to another Save to, like the shown item's, and survive Refresh game data.</summary>
    [Fact]
    public void AWaitingItemsChanges_FollowSaveTo_AndARefresh()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.SelectedItem = Item(vm, "Stone");

        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

        Assert.Equal(1, vm.PendingCount);
        vm.SelectedItem = Item(vm, "Envirosuit");
        Assert.Equal("50", Weight(vm).ValueText);
    }

    /// <summary>A placeable item with a spoiling row: its on/off fields (only the ones with plain meaning) and what it spoils into.</summary>
    private void WriteDeployableStone()
    {
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"},"Decayable":{"RowName":"None","DataTableName":"D_Decayable"},"Armour":{"RowName":"None","DataTableName":"D_Armour"},"Deployable":{"RowName":"None","DataTableName":"D_Deployable"}},"Rows":[
              {"Name":"Envirosuit","Itemable":{"RowName":"Item_Envirosuit"},"Decayable":{"RowName":"Decay_General"},"Armour":{"RowName":"IO_Undersuit"}},
              {"Name":"Stone","Itemable":{"RowName":"Item_Stone"},"Decayable":{"RowName":"Decay_Stone"},"Deployable":{"RowName":"Deploy_Stone"}}
            ]}
            """);
        Write("Traits/D_Deployable.json", """{"RowStruct":"S","Defaults":{"EffectedByWeather":true,"bMustBeOutside":false,"bUseActorPooling":true},"Rows":[{"Name":"Deploy_Stone","bMustBeOutside":true}]}""");
        Write("Traits/D_Decayable.json", """{"RowStruct":"S","Defaults":{"SpoiledItem":{"RowName":"None","DataTableName":"D_ItemTemplate"}},"Rows":[{"Name":"Decay_General","DecayTime":300},{"Name":"Decay_Stone","DecayTime":300,"SpoiledItem":{"RowName":"Stone"}}]}""");
        Write("Items/D_ItemTemplate.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone","ItemStaticData":{"RowName":"Stone"}},{"Name":"Envirosuit","ItemStaticData":{"RowName":"Envirosuit"}}]}""");
    }

    /// <summary>On/off fields are tick boxes (only ones with plain meaning: not actor pooling), the table's default counts, and turning one off is saved as false.</summary>
    [Fact]
    public void OnOffFields_AreTickBoxes_AndSaveAsTrueOrFalse()
    {
        WriteDeployableStone();
        var vm = Open();
        vm.SelectedItem = Item(vm, "Stone");

        var placing = vm.Sections.Single(s => s.Section.Key == "Deployable");
        Assert.Equal(["Must be outside", "Weather damages it"], placing.Fields.Where(f => f.IsToggle).Select(f => f.Label).Order());
        var weather = placing.Fields.Single(f => f.Label == "Weather damages it");
        Assert.True(weather.IsOn);
        Assert.Equal("game: on (table default)", weather.GameText);

        weather.IsOn = false;
        Assert.Equal(1, vm.PendingCount);
        vm.NewModName = "Weatherproof";
        vm.SaveCommand.Execute(null);

        var row = Read(_library, Assert.Single(_library.GetAll()).FolderName).Rows.Single(r => r.CurrentFile == "Traits-D_Deployable.json").FileItems.Single();
        Assert.Equal("Deploy_Stone", row.Name);
        Assert.False(row.Fields["EffectedByWeather"]!.GetValue<bool>());
    }

    /// <summary>What it spoils into is a drop-down of item templates by their in-game names; a name that isn't a row can't be saved.</summary>
    [Fact]
    public void SpoilsInto_IsADropDown_OfRealRows()
    {
        WriteDeployableStone();
        var vm = Open();
        vm.SelectedItem = Item(vm, "Stone");

        var spoils = vm.Sections.Single(s => s.Section.Key == "Decayable").Fields.Single(f => f.Label == "Spoils into");
        Assert.True(spoils.IsLink);
        Assert.Equal(["None", "Stone", "Envirosuit"], spoils.LinkChoices!.Select(c => c.Name));
        Assert.Equal("Xigo Envirosuit  (Envirosuit)", spoils.LinkChoices!.Single(c => c.Name == "Envirosuit").Display);

        spoils.LinkValue = null; // a search typed into the drop-down that matches nothing
        Assert.Equal("Stone", spoils.ValueText);
        spoils.ValueText = "Not_A_Row";
        Assert.Equal("Pick one from the list.", spoils.Error);
        spoils.LinkValue = "envirosuit";
        Assert.Null(spoils.Error);
        Assert.Equal("Envirosuit", spoils.NewValue()!.GetValue<string>()); // as the game spells it
    }

    /// <summary>The page reads the game's items in the background the first time it shows, saying so meanwhile.</summary>
    [Fact]
    public async Task TheItemList_LoadsInTheBackground_SayingSo()
    {
        var vm = new GameEditorViewModel(_library, DataFolder);

        var loading = vm.EnsureLoadedAsync();
        Assert.Equal(GameEditorViewModel.LoadingMessage, vm.NoDataMessage);
        Assert.False(vm.ShowPickPrompt);
        await loading;

        Assert.Null(vm.NoDataMessage);
        Assert.Equal(["Envirosuit", "Stone"], Names(vm));
        Assert.True(vm.ShowPickPrompt);
    }

    /// <summary>"Open in Editor" while the list is still loading waits for it (the tables aren't read twice, nothing is listed twice).</summary>
    [Fact]
    public async Task OpenDuringTheBackgroundLoad_WaitsForIt()
    {
        var vm = new GameEditorViewModel(_library, DataFolder);
        var loading = vm.EnsureLoadedAsync();

        vm.Open("Envirosuit");
        await loading;

        Assert.Equal("Envirosuit", vm.SelectedItem?.Name);
        Assert.Equal(["Envirosuit", "Stone"], Names(vm));
        Assert.True(vm.HasItem);
    }

    /// <summary>The page listens and speaks on the messenger it's given (the app's shared one by default).</summary>
    [Fact]
    public void ThePage_UsesTheMessengerItIsGiven()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new GameEditorViewModel(_library, DataFolder, messenger: messenger);
        vm.EnsureLoaded();
        _library.CreateBlankMod("Mine", "Me");

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        Assert.Equal(["A new mod"], vm.Targets.Select(t => t.Display));

        messenger.Send(new LibraryChangedMessage());
        Assert.Equal(["A new mod", "Mine"], vm.Targets.Select(t => t.Display));

        var asked = new UnsavedEditsRequestMessage();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        messenger.Send(asked);
        Assert.Contains("typed changes on the Editor page", asked.Responses);
    }

    private void WriteEnvirosuitRecipe() => Write("Crafting/D_ProcessorRecipes.json", """
        {"RowStruct":"S","Defaults":{},"Rows":[
          {"Name":"Envirosuit_Recipe","RecipeSets":[{"RowName":"Character","DataTableName":"D_RecipeSets"}],
           "Inputs":[{"Element":{"RowName":"Stone","DataTableName":"D_ItemsStatic"},"Count":4},{"Element":{"RowName":"Leather","DataTableName":"D_ItemsStatic"},"Count":2}],
           "Outputs":[{"Element":{"RowName":"Envirosuit","DataTableName":"D_ItemTemplate"},"Count":1}]}
        ]}
        """);

    /// <summary>An ingredient's ✕ takes it out, "Add ingredient" adds one: Save writes the recipe's whole new list, numbers as typed.</summary>
    [Fact]
    public void Ingredients_TakenOutAndAdded_SaveAsTheWholeNewList()
    {
        WriteEnvirosuitRecipe();
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        var recipe = vm.Sections.Single(s => s.Section.Key == "Recipe:Envirosuit_Recipe");
        Assert.True(recipe.CanEditIngredients);

        var leather = recipe.Fields.Single(f => f.Label == "Needs Leather");
        leather.DropCommand.Execute(null);
        Assert.False(leather.ShowDrop);
        recipe.Fields.Single(f => f.Label == "Needs Stone").ValueText = "3";

        recipe.IngredientPicker!.ItemSearch = "stone";
        recipe.IngredientPicker.SelectedItem = recipe.IngredientPicker.MatchingItems.Single();
        recipe.IngredientPicker.AddItemCommand.Execute(null);
        Assert.Equal("Already an ingredient: change its number instead.", recipe.IngredientPicker.ItemError);

        recipe.IngredientPicker.ItemSearch = "xigo";
        recipe.IngredientPicker.SelectedItem = recipe.IngredientPicker.MatchingItems.Single();
        recipe.IngredientPicker.AddItemCommand.Execute(null);
        recipe.Fields.Single(f => f.Label == "Needs Xigo Envirosuit").ValueText = "5";

        Assert.Equal(3, vm.PendingCount);
        Assert.Contains(vm.PendingGroups.SelectMany(g => g.Changes), c => c.Text.EndsWith("Needs Leather: taken out", StringComparison.Ordinal));

        vm.NewModName = "Cheaper suit";
        vm.SaveCommand.Execute(null);

        var inputs = (System.Text.Json.Nodes.JsonArray)Read(_library, Assert.Single(_library.GetAll()).FolderName)
            .Rows.Single(r => r.CurrentFile == "Crafting-D_ProcessorRecipes.json").FileItems.Single().Fields["Inputs"]!;
        Assert.Equal(["Stone:3", "Envirosuit:5"], inputs.Select(e => $"{e!["Element"]!["RowName"]}:{e["Count"]}"));
        Assert.Equal("D_ItemsStatic", inputs[1]!["Element"]!["DataTableName"]!.GetValue<string>());
    }

    /// <summary>↺ on a taken-out ingredient puts it back (and so does its × in Your changes): nothing left to save.</summary>
    [Fact]
    public void ATakenOutIngredient_GoesBackIn()
    {
        WriteEnvirosuitRecipe();
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        var leather = vm.Sections.Single(s => s.Section.Key == "Recipe:Envirosuit_Recipe").Fields.Single(f => f.Label == "Needs Leather");

        leather.DropCommand.Execute(null);
        Assert.True(leather.DiffersFromGame);
        leather.UseGameValueCommand.Execute(null);
        Assert.Equal(0, vm.PendingCount);

        leather.DropCommand.Execute(null);
        vm.PendingGroups.SelectMany(g => g.Changes).Single().Field.UndoChangeCommand.Execute(null);
        Assert.False(leather.IsDropped);
        Assert.Equal(0, vm.PendingCount);
    }

    private void WriteBenches() => Write("Crafting/D_RecipeSets.json", """
        {"RowStruct":"S","Defaults":{},"Rows":[
          {"Name":"Character","RecipeSetName":"NSLOCTEXT(\"D_RecipeSets\", \"Character-RecipeSetName\", \"Character\")"},
          {"Name":"Fabricator","RecipeSetName":"NSLOCTEXT(\"D_RecipeSets\", \"Fabricator-RecipeSetName\", \"Fabricator\")"},
          {"Name":"Manufacturer","RecipeSetName":"NSLOCTEXT(\"D_RecipeSets\", \"Manufacturer-RecipeSetName\", \"Manufacturer\")"}
        ]}
        """);

    /// <summary>
    /// "Add bench" makes a recipe at another bench too, and a bench's ✕ takes one out: Save writes
    /// the recipe's whole new bench list. Moving a recipe = add the new bench, ✕ the old one.
    /// </summary>
    [Fact]
    public void Benches_AddedAndTakenOut_SaveAsTheWholeNewList()
    {
        WriteEnvirosuitRecipe();
        WriteBenches();
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        var recipe = vm.Sections.Single(s => s.Section.Key == "Recipe:Envirosuit_Recipe");
        Assert.True(recipe.HasBenchPicker);
        Assert.DoesNotContain(recipe.BenchChoices!, c => c.Name == "None");

        var byHand = recipe.Fields.Single(f => f.Label == "Made at");
        Assert.False(byHand.ShowDrop); // the only bench: a recipe needs one

        recipe.AddBenchCommand.Execute(null);
        Assert.Equal("Pick a bench from the list first.", recipe.BenchError);
        recipe.BenchToAdd = recipe.BenchChoices!.Single(c => c.Name == "Character");
        recipe.AddBenchCommand.Execute(null);
        Assert.Equal("It's already made there.", recipe.BenchError);

        recipe.BenchToAdd = recipe.BenchChoices!.Single(c => c.Name == "Fabricator");
        recipe.AddBenchCommand.Execute(null);
        Assert.Null(recipe.BenchError);
        var fabricator = recipe.Fields.Single(f => f.Label == "Made at (2)");
        Assert.Equal("added by you", fabricator.GameText);
        Assert.Equal(recipe.Fields.IndexOf(byHand) + 1, recipe.Fields.IndexOf(fabricator)); // with the other benches, not after the ingredients
        Assert.True(byHand.ShowDrop);

        byHand.DropCommand.Execute(null);
        Assert.True(byHand.IsDropped);
        Assert.False(fabricator.ShowDrop); // now the last one left

        vm.NewModName = "Fabricated suit";
        vm.SaveCommand.Execute(null);

        var benches = (System.Text.Json.Nodes.JsonArray)Read(_library, Assert.Single(_library.GetAll()).FolderName)
            .Rows.Single(r => r.CurrentFile == "Crafting-D_ProcessorRecipes.json").FileItems.Single().Fields["RecipeSets"]!;
        var bench = Assert.Single(benches)!;
        Assert.Equal("Fabricator", bench["RowName"]!.GetValue<string>());
        Assert.Equal("D_RecipeSets", bench["DataTableName"]!.GetValue<string>());
    }

    /// <summary>↺ on an added bench takes it out again; when the game's bench was taken out meanwhile, it comes back instead of leaving none.</summary>
    [Fact]
    public void RemovingTheOnlyBenchLeft_BringsTheGamesBack()
    {
        WriteEnvirosuitRecipe();
        WriteBenches();
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        var recipe = vm.Sections.Single(s => s.Section.Key == "Recipe:Envirosuit_Recipe");
        var byHand = recipe.Fields.Single(f => f.Label == "Made at");
        recipe.BenchToAdd = recipe.BenchChoices!.Single(c => c.Name == "Manufacturer");
        recipe.AddBenchCommand.Execute(null);
        byHand.DropCommand.Execute(null);

        recipe.Fields.Single(f => f.Label == "Made at (2)").UseGameValueCommand.Execute(null);

        Assert.False(byHand.IsDropped);
        Assert.False(byHand.ShowDrop);
        Assert.Equal(0, vm.PendingCount);
    }

    /// <summary>In the real game data, 47 items' text ends in a space ("…exposure resistance. "), and opening one showed a change nobody made.</summary>
    [Fact]
    public void GameTextEndingInASpace_IsNotAChange()
    {
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0,"MaxStack":1},"Rows":[
              {"Name":"Item_Envirosuit","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-DisplayName\", \"Xigo Envirosuit\")",
               "Description":"NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-Description\", \"A suit. \")","Weight":100},
              {"Name":"Item_Stone","Weight":10}
            ]}
            """);
        var vm = Open();

        vm.SelectedItem = Item(vm, "Envirosuit");

        var description = vm.Sections.Single(s => s.Section.Key == "Itemable").Fields.Single(f => f.Label == "Description");
        Assert.False(description.DiffersFromGame);
        Assert.Equal(0, vm.PendingCount);
        Assert.False(Item(vm, "Envirosuit").HasUnsavedChanges);
    }

    private static void PickBulkField(GameEditorViewModel vm, string section, string path) =>
        vm.SelectedBulkField = vm.BulkFields.Single(f => f.Key == new BulkFieldKey(section, path));

    /// <summary>Tick items, pick a value they share, multiply: the preview shows each item's before and after, and the values go into Your changes like typed ones.</summary>
    [Fact]
    public void BulkEdit_MultipliesEveryTickedItem_IntoYourChanges_ThenOneSave()
    {
        var vm = Open();
        vm.TickAllShownCommand.Execute(null);
        Assert.Equal(2, vm.TickedCount);

        vm.OpenBulkCommand.Execute(null);
        Assert.False(vm.ShowPickPrompt);
        PickBulkField(vm, "Itemable", "Weight");
        Assert.Equal(2, vm.SelectedBulkField!.ItemCount);
        vm.SelectedBulkOperation = BulkEdit.Operations.Single(o => o.Operation == BulkOperation.Multiply);
        vm.BulkValueText = "0.5";

        Assert.Equal(["100 → 50", "10 → 5"], vm.BulkRows.Select(r => $"{r.CurrentText} → {r.NewText}"));
        Assert.Equal(0, vm.PendingCount); // only a preview

        vm.ApplyBulkCommand.Execute(null);

        Assert.Equal(2, vm.PendingCount);
        Assert.Equal(2, vm.EditedItemCount);
        Assert.Empty(vm.BulkRows); // not previewed again from the new values: a second click would halve twice
        Assert.StartsWith("Done: 2 item(s)", vm.BulkSummary);
        vm.NewModName = "Light";
        vm.SaveCommand.Execute(null);
        var rows = Read(_library, Assert.Single(_library.GetAll()).FolderName).Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems;
        Assert.Equal(50, rows.Single(i => i.Name == "Item_Envirosuit").Fields["Weight"]!.GetValue<int>());
        Assert.Equal(5, rows.Single(i => i.Name == "Item_Stone").Fields["Weight"]!.GetValue<int>());
    }

    /// <summary>The decay row Stone uses too: the Envirosuit gets its own copy by default (Stone's decay stays); "Change the shared rows" changes the shared row, for Stone as well.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BulkEdit_OnASharedRow_OwnCopyOrTheSharedRow(bool ownCopy)
    {
        var vm = Open();
        Item(vm, "Envirosuit").IsTicked = true;
        vm.OpenBulkCommand.Execute(null);
        PickBulkField(vm, "Decayable", "DecayTime");
        vm.BulkValueText = "2";
        Assert.NotNull(vm.BulkSharedText);
        vm.BulkOwnCopy = ownCopy;

        vm.ApplyBulkCommand.Execute(null);
        vm.NewModName = "Slow decay";
        vm.SaveCommand.Execute(null);

        var row = Assert.Single(Read(_library, Assert.Single(_library.GetAll()).FolderName).Rows.Single(r => r.CurrentFile == "Traits-D_Decayable.json").FileItems);
        Assert.Equal(ownCopy ? "Decay_General_Envirosuit" : "Decay_General", row.Name);
        Assert.Equal(600, row.Fields["DecayTime"]!.GetValue<int>());
    }

    /// <summary>The bulk edit starts from what's typed (the shown item's 80, not its 100); an unticked preview row is left out; Undo takes it all back out.</summary>
    [Fact]
    public void BulkEdit_StartsFromTypedValues_LeavesOutUntickedRows_AndUndoes()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "80";
        vm.TickAllShownCommand.Execute(null);
        vm.OpenBulkCommand.Execute(null);
        PickBulkField(vm, "Itemable", "Weight");
        vm.BulkValueText = "0.5";

        Assert.Equal("80 → 40", vm.BulkRows.Single(r => r.Item.Name == "Envirosuit").CurrentText + " → " + vm.BulkRows.Single(r => r.Item.Name == "Envirosuit").NewText);
        vm.BulkRows.Single(r => r.Item.Name == "Stone").Include = false;
        vm.ApplyBulkCommand.Execute(null);

        Assert.Equal("40", Weight(vm).ValueText);
        Assert.Equal(["Envirosuit"], vm.PendingGroups.Select(g => g.ItemName));
        Assert.True(vm.CanUndoBulk);

        vm.UndoBulkCommand.Execute(null);

        Assert.Equal("80", Weight(vm).ValueText);
        Assert.Equal(1, vm.PendingCount);
        Assert.False(vm.CanUndoBulk);
    }

    /// <summary>A value's chip ticks the items the list shows that have it (here: the Armour category) and picks it; ×1.5 is one click; nothing changes until it's put in.</summary>
    [Fact]
    public void AValuesChip_TicksTheListedItems_AndAQuickButtonSetsTheNumber()
    {
        var vm = Open();
        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Armour");

        vm.PickShortcutCommand.Execute(BulkEdit.Shortcuts.Single(s => s.Label == "Weight"));
        vm.MultiplyByCommand.Execute("1.5");

        Assert.True(vm.BulkMode);
        vm.ClearFiltersCommand.Execute(null); // every item listed again: Stone (not armour) wasn't ticked
        Assert.Equal(["Envirosuit"], vm.FilteredItems.Where(i => i.IsTicked).Select(i => i.Name));
        Assert.Equal(new BulkFieldKey("Itemable", "Weight"), vm.SelectedBulkField?.Key);
        Assert.Equal(BulkOperation.Multiply, vm.SelectedBulkOperation.Operation);
        Assert.Equal("100 → 150", $"{vm.BulkRows.Single().CurrentText} → {vm.BulkRows.Single().NewText}");
        Assert.Equal(0, vm.PendingCount);

        vm.SelectedItem = Item(vm, "Envirosuit");
        Assert.False(vm.BulkMode); // picking an item shows it
    }

    /// <summary>One item's new value typed over: it gets that instead, the edit survives another number, and a bad one blocks putting it in with the item's name.</summary>
    [Fact]
    public void APreviewRow_TypedOver_GivesThatItemItsOwnValue()
    {
        var vm = Open();
        vm.TickAllShownCommand.Execute(null);
        vm.OpenBulkCommand.Execute(null);
        PickBulkField(vm, "Itemable", "Weight");
        vm.MultiplyByCommand.Execute("2");

        var stone = vm.BulkRows.Single(r => r.Item.Name == "Stone");
        stone.NewText = "7.5";
        Assert.Equal("Whole numbers only here.", stone.Error);
        vm.ApplyBulkCommand.Execute(null);
        Assert.Equal("Stone: Whole numbers only here.", vm.BulkError);
        Assert.Equal(0, vm.PendingCount);

        stone.NewText = "7";
        vm.MultiplyByCommand.Execute("3"); // the preview is rebuilt: Stone keeps its 7
        Assert.Equal("7", vm.BulkRows.Single(r => r.Item.Name == "Stone").NewText);
        Assert.True(vm.BulkRows.Single(r => r.Item.Name == "Stone").IsEdited);
        Assert.Equal("300", vm.BulkRows.Single(r => r.Item.Name == "Envirosuit").NewText);

        vm.ApplyBulkCommand.Execute(null);
        vm.SelectedItem = Item(vm, "Stone");
        Assert.Equal("7", Weight(vm).ValueText);
        vm.SelectedItem = Item(vm, "Envirosuit");
        Assert.Equal("300", Weight(vm).ValueText);
    }

    /// <summary>A waiting item with a value that isn't a number blocks Save, naming the item.</summary>
    [Fact]
    public void ABadNumberOnAWaitingItem_BlocksSaving_NamingTheItem()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "heavy";
        vm.SelectedItem = Item(vm, "Stone");
        Weight(vm).ValueText = "5";
        vm.NewModName = "X";

        vm.SaveCommand.Execute(null);

        Assert.Equal("Xigo Envirosuit · Weight: Needs a number, like 50 or 0.5.", vm.StatusMessage);
        Assert.Empty(_library.GetAll());
        Assert.True(Item(vm, "Envirosuit").HasUnsavedChanges);
    }

    /// <summary>Regression:changing "Save to" with typed changes showed one mod's values and saved into another.</summary>
    [Fact]
    public void ChangingWhereToSave_KeepsTheTypedChanges()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";

        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);

        Assert.Equal("50", Weight(vm).ValueText);
        Assert.Equal(1, vm.PendingCount);
    }

    [Fact]
    public void AnAddedStat_CanBeTakenOutAgain_AndWholeNumbersStayWhole()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        var armour = vm.Sections.Single(s => s.Section.Key == "Armour");
        armour.Picker.StatSearch = "cold";
        armour.Picker.SelectedStat = armour.Picker.MatchingStats.Single();
        armour.Picker.AddStatCommand.Execute(null);
        var cold = armour.Fields.Single(f => f.Label == "Cold resistance %");

        cold.UseGameValueCommand.Execute(null);

        Assert.DoesNotContain(cold, armour.Fields);
        Assert.Equal(0, vm.PendingCount);

        Weight(vm).ValueText = "2.5";
        Assert.Equal("Needs a whole number here, like 50.", Weight(vm).Error);
    }

    /// <summary>Stats are whole numbers in the game: an added stat and a game stat both refuse 5.5.</summary>
    [Fact]
    public void Stats_NeedWholeNumbers()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        var armour = vm.Sections.Single(s => s.Section.Key == "Armour");
        armour.Picker.StatSearch = "cold";
        armour.Picker.SelectedStat = armour.Picker.MatchingStats.Single();
        armour.Picker.AddStatCommand.Execute(null);
        var cold = armour.Fields.Single(f => f.Label == "Cold resistance %");
        var oxygen = armour.Fields.Single(f => f != cold);

        cold.ValueText = "5.5";
        oxygen.ValueText = "1.5";

        Assert.Contains("whole number", cold.Error);
        Assert.Contains("whole number", oxygen.Error);
        cold.ValueText = "5";
        Assert.Null(cold.Error);
    }

    /// <summary>Whole-or-not is per field: Weight 100 on this item is still a real number when another item weighs 0.5.</summary>
    [Fact]
    public void Number_WholeHereButDecimalElsewhereInTheTable_TakesAFraction()
    {
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0,"MaxStack":1},"Rows":[
              {"Name":"Item_Envirosuit","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-DisplayName\", \"Xigo Envirosuit\")","Weight":100},
              {"Name":"Item_Stone","Weight":0.5}
            ]}
            """);
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");

        Weight(vm).ValueText = "2.5";

        Assert.Null(Weight(vm).Error);
    }

    /// <summary>Regression:every Library change rebuilt the targets and reloaded the item, dropping a stat added here and not saved yet.</summary>
    [Fact]
    public void AnAddedStat_SurvivesALibraryChange()
    {
        _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        var armour = vm.Sections.Single(s => s.Section.Key == "Armour");
        armour.Picker.StatSearch = "cold";
        armour.Picker.SelectedStat = armour.Picker.MatchingStats.Single();
        armour.Picker.AddStatCommand.Execute(null);
        armour.Fields.Single(f => f.Label == "Cold resistance %").ValueText = "15";
        // What WPF's ComboBox does when its items are cleared under the selection.
        vm.Targets.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                vm.SelectedTarget = null;
            }
        };

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        Assert.Equal("15", vm.Sections.Single(s => s.Section.Key == "Armour").Fields.Single(f => f.Label == "Cold resistance %").ValueText);
        Assert.Equal(1, vm.PendingCount);
        Assert.True(vm.IsNewTarget);
    }

    /// <summary>Regression:↺ on a stat the target mod adds emptied the box ("Needs a number") and blocked saving; it now takes the stat out.</summary>
    [Fact]
    public void UseGameValue_OnAStatTheModAdds_TakesItOut()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        var armour = vm.Sections.Single(s => s.Section.Key == "Armour");
        armour.Picker.StatSearch = "cold";
        armour.Picker.SelectedStat = armour.Picker.MatchingStats.Single();
        armour.Picker.AddStatCommand.Execute(null);
        armour.Fields.Single(f => f.Label == "Cold resistance %").ValueText = "15";
        vm.SaveCommand.Execute(null);
        Assert.Contains("ColdResistance", ExmodJson.Serialize(Read(_library, mine.FolderName)));

        var cold = vm.Sections.Single(s => s.Section.Key == "Armour").Fields.Single(f => f.Label == "Cold resistance %");
        cold.UseGameValueCommand.Execute(null);

        Assert.Null(cold.Error);
        Assert.Equal(1, vm.PendingCount);
        vm.SaveCommand.Execute(null);
        Assert.StartsWith("Saved", vm.StatusMessage);
        Assert.DoesNotContain("ColdResistance", ExmodJson.Serialize(Read(_library, mine.FolderName)));
    }

    /// <summary>Regression:"Open in Editor" from a mod kept the page's own target, so it showed (and saved) the game's values.</summary>
    [Fact]
    public void OpenFromAMod_MakesThatModTheTarget()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();

        WeakReferenceMessenger.Default.Send(new OpenGameItemMessage("Envirosuit", mine.FolderName));

        Assert.Equal("Envirosuit", vm.SelectedItem?.Name);
        Assert.Equal(mine.FolderName, vm.SelectedTarget?.FolderName);
    }

    /// <summary>A mod imported from a file is someone else's even after editing it here: never a save target.</summary>
    [Fact]
    public void AnImportedModEditedHere_IsNotOffered()
    {
        var source = Path.Combine(_root, "_friend");
        ExmodFolder.Write(source, new ExmodPackageContents(
            new ExmodPackage { Name = "Friend's mod", Author = "Friend", Version = "1", Description = "", FileName = "Friends_Mod", Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }] }, []));
        var imported = _library.Import(source);
        _library.MarkLocallyEdited(imported.FolderName);

        Assert.Equal(["A new mod"], Open().Targets.Select(t => t.Display));
    }

    /// <summary>Closing the app (or updating it) asks first when values are typed here and not saved.</summary>
    [Fact]
    public void TypedChanges_AreReportedAsUnsavedWork()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");

        Weight(vm).ValueText = "50";

        Assert.Contains("typed changes on the Editor page", IcarusStarlink.App.Services.AppBusyCheck.UnsavedEdits());
    }

    /// <summary>Regression:Refresh game data reloaded the item and threw away every typed value.</summary>
    [Fact]
    public void RefreshGameData_KeepsTheTypedChanges()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";

        WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

        Assert.Equal("Envirosuit", vm.SelectedItem?.Name);
        Assert.Equal("50", Weight(vm).ValueText);
        Assert.Equal(1, vm.PendingCount);
    }

    /// <summary>The mod editor saved into the target mod after its values were shown here: the first Save shows the new version (typed values kept) instead of saving over it.</summary>
    [Fact]
    public void Save_WhenTheModChangedSinceItWasShown_ShowsTheNewVersionFirst()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        Weight(vm).ValueText = "50";

        var changed = Read(_library, mine.FolderName);
        changed.Rows.Insert(0, new ExmodFileRow { CurrentFile = "Traits-D_Itemable.json", FileItems = [new ExmodFileItem { Name = "Item_Stone", Fields = { ["Weight"] = 5 } }] });
        var folder = _library.GetFolderPath(mine.FolderName);
        ExmodFolder.Write(folder, new ExmodPackageContents(changed, []));
        var exmod = Directory.GetFiles(Path.Combine(folder, "Extracted Mods"), "*.EXMOD").Single();
        File.SetLastWriteTimeUtc(exmod, DateTime.UtcNow.AddMinutes(1));

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Not saved", vm.StatusMessage);
        Assert.DoesNotContain("Item_Envirosuit", ExmodJson.Serialize(Read(_library, mine.FolderName)));
        Assert.Equal("50", Weight(vm).ValueText);

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        var saved = ExmodJson.Serialize(Read(_library, mine.FolderName));
        Assert.Contains("Item_Stone", saved);
        Assert.Contains("Item_Envirosuit", saved);
    }

    private static string[] Names(GameEditorViewModel vm) => [.. vm.FilteredItems.Select(i => i.Name)];

    /// <summary>Every item is listed (it stopped at 400 of 3,090), with a chip per category the game files its icons under; each chip counts what it would show under the search.</summary>
    [Fact]
    public void Categories_NarrowTheList_AndCountUnderTheSearch()
    {
        var vm = Open();
        Assert.Equal(["All", "Armour", "Resources"], vm.Categories.Select(c => c.Label));
        Assert.Equal("All 2 items", vm.ShowingText);

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Armour");
        Assert.Equal(["Envirosuit"], Names(vm));
        Assert.Equal("1 of 2 items", vm.ShowingText);

        vm.SelectedCategory = vm.Categories[0];
        vm.FilterText = "xigo";
        vm.FlushFilter();
        Assert.Equal([1, 1, 0], vm.Categories.Select(c => c.Count));
    }

    /// <summary>A search also finds what the game says about an item (after the name matches); Clear filters brings everything back.</summary>
    [Fact]
    public void Search_FindsAnItemByItsDescription_AndClearFiltersShowsAllAgain()
    {
        var vm = Open();

        vm.FilterText = "rock";
        vm.FlushFilter();
        Assert.Equal(["Stone"], Names(vm));

        vm.FilterText = "nothing like this";
        vm.FlushFilter();
        Assert.True(vm.HasNoMatches);

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal("", vm.FilterText);
        Assert.Equal(["Envirosuit", "Stone"], Names(vm));
        Assert.False(vm.HasNoMatches);
    }

    [Fact]
    public void HasFilter_KeepsTheItemsWithThatCard()
    {
        var vm = Open();

        vm.SelectedHas = vm.HasChoices.Single(c => c.Key == "Armour");

        Assert.Equal(["Envirosuit"], Names(vm));
    }

    /// <summary>The items the mod under Save to changes get a dot, and can be the only ones shown; "A new mod" changes nothing yet.</summary>
    [Fact]
    public void ItemsTheTargetModChanges_AreMarked_AndCanBeTheOnlyOnesShown()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.NewModName = "Lighter Suit";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.IsOwnModTarget);
        Assert.True(vm.FilteredItems.Single(i => i.Name == "Envirosuit").IsInTargetMod);
        Assert.False(vm.FilteredItems.Single(i => i.Name == "Stone").IsInTargetMod);
        vm.OnlyInTargetMod = true;
        Assert.Equal(["Envirosuit"], Names(vm));
        Assert.Equal("Only the 1 item this mod changes", vm.TargetModText);

        vm.SelectedTarget = vm.Targets[0];

        Assert.All(vm.FilteredItems, i => Assert.False(i.IsInTargetMod));
        Assert.Equal(["Envirosuit", "Stone"], Names(vm));
    }

    /// <summary>The item on show stays in the list when a filter would hide it (on top), and is there once, not twice, after Refresh game data rebuilds the list.</summary>
    [Fact]
    public void TheItemOnShow_StaysListed_AndOnlyOnceAfterARefresh()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");

        vm.SelectedCategory = vm.Categories.Single(c => c.Label == "Resources");
        Assert.Equal(["Envirosuit", "Stone"], Names(vm));
        Assert.Equal("1 of 2 items", vm.ShowingText);

        vm.SelectedCategory = vm.Categories[0];
        WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

        Assert.Equal(["Envirosuit", "Stone"], Names(vm));
        Assert.Same(vm.FilteredItems[0], vm.SelectedItem);
    }

    /// <summary>Puts rows into one of the user's own mods straight on disk (what the mod editor or a fix would write).</summary>
    private void WriteIntoMod(string folder, params ExmodFileRow[] rows)
    {
        var package = Read(_library, folder);
        package.Rows.InsertRange(0, rows);
        ExmodFolder.Write(_library.GetFolderPath(folder), new ExmodPackageContents(package, []));
    }

    private static ExmodFileRow ModRow(string file, string rowName, string fieldsJson) =>
        new() { CurrentFile = file, FileItems = [ExmodFileItem.FromRow(rowName, JsonNode.Parse(fieldsJson)!.AsObject())] };

    /// <summary>Moves the mod's .EXMOD stamp on, as a save from somewhere else does.</summary>
    private void TouchExmod(string folder)
    {
        var exmod = Directory.GetFiles(Path.Combine(_library.GetFolderPath(folder), "Extracted Mods"), "*.EXMOD").Single();
        File.SetLastWriteTimeUtc(exmod, DateTime.UtcNow.AddMinutes(1));
    }

    private JsonArray SavedList(string folder, string field) =>
        (JsonArray)Read(_library, folder).Rows.Single(r => r.CurrentFile == "Crafting-D_ProcessorRecipes.json").FileItems.Single().Fields[field]!;

    private static GameItemSectionViewModel Recipe(GameEditorViewModel vm) => vm.Sections.Single(s => s.Section.Key == "Recipe:Envirosuit_Recipe");

    /// <summary>Regression: the shown item reloading after the mod changed elsewhere took the new stamp without rebuilding the waiting items, so Save wrote their lists from the old file.</summary>
    [Fact]
    public void AWaitingItem_IsRebuilt_WhenTheModChangedElsewhereWhileAnotherItemShows()
    {
        WriteEnvirosuitRecipe();
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = Item(vm, "Envirosuit");
        Recipe(vm).Fields.Single(f => f.Label == "Needs Leather").DropCommand.Execute(null);
        vm.SelectedItem = Item(vm, "Stone"); // Envirosuit waits; nothing typed on Stone

        WriteIntoMod(mine.FolderName, ModRow("Crafting-D_ProcessorRecipes.json", "Envirosuit_Recipe", """
            {"Inputs":[{"Element":{"RowName":"Stone","DataTableName":"D_ItemsStatic"},"Count":4},
                       {"Element":{"RowName":"Leather","DataTableName":"D_ItemsStatic"},"Count":2},
                       {"Element":{"RowName":"Envirosuit","DataTableName":"D_ItemsStatic"},"Count":1}]}
            """));
        TouchExmod(mine.FolderName);
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        vm.SaveCommand.Execute(null);

        Assert.Equal(["Stone", "Envirosuit"], SavedList(mine.FolderName, "Inputs").Select(e => e!["Element"]!["RowName"]!.GetValue<string>()));
    }

    private int BackupCount() =>
        Directory.Exists(Path.Combine(_root, "Backups")) ? Directory.GetDirectories(Path.Combine(_root, "Backups")).Length : 0;

    /// <summary>A Save into one of your mods backs it up first (Restore latest backup undoes that Save); a new mod has nothing to back up.</summary>
    [Fact]
    public void Save_IntoAnExistingMod_BacksItUpFirst_ButNotANewMod()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.NewModName = "Lighter";
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, BackupCount());

        Weight(vm).ValueText = "40";
        vm.SaveCommand.Execute(null);
        Assert.Equal(1, BackupCount());
        Weight(vm).ValueText = "30";
        vm.SaveCommand.Execute(null);

        Assert.Equal(2, BackupCount());
        Assert.Contains("backed up first", vm.StatusMessage);
        var folder = Assert.Single(_library.GetAll()).FolderName;
        var latest = Directory.GetFiles(_library.TryGetLatestModBackupPath(folder)!, "*.EXMOD", SearchOption.AllDirectories).Single();
        // The mod as it was right before this Save.
        var weight = ExmodJson.Parse(File.ReadAllText(latest)).Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single().Fields["Weight"]!;
        Assert.Equal(40, weight.GetValue<double>());
    }

    /// <summary>Regression: "Put into Your changes" swapped a waiting item's typed cards for the cards an earlier preview built.</summary>
    [Fact]
    public void BulkEdit_AfterAnEarlierPreview_KeepsAWaitingItemsTypedChanges()
    {
        var vm = Open();
        vm.TickAllShownCommand.Execute(null);
        vm.OpenBulkCommand.Execute(null); // the preview builds both items' cards
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.SelectedItem = Item(vm, "Stone"); // Envirosuit waits with its 50

        vm.OpenBulkCommand.Execute(null);
        PickBulkField(vm, "Itemable", "Weight");
        vm.MultiplyByCommand.Execute("3");
        var suit = vm.BulkRows.Single(r => r.Item.Name == "Envirosuit");
        Assert.Equal("50 → 150", $"{suit.CurrentText} → {suit.NewText}");
        vm.ApplyBulkCommand.Execute(null);

        Assert.Equal(["Stone", "Envirosuit"], vm.PendingGroups.Select(g => g.ItemName));
        Assert.Equal(2, vm.EditedItemCount);

        vm.UndoBulkCommand.Execute(null);
        vm.SelectedItem = Item(vm, "Envirosuit");
        Assert.Equal("50", Weight(vm).ValueText);
    }

    /// <summary>Regression: a typed amount was carried to another Save to by its position, landing on another ingredient without a word.</summary>
    [Fact]
    public void ATypedIngredient_FollowsItsItem_NotItsPosition()
    {
        WriteEnvirosuitRecipe();
        var mine = _library.CreateBlankMod("Mine", "Me");
        WriteIntoMod(mine.FolderName, ModRow("Crafting-D_ProcessorRecipes.json", "Envirosuit_Recipe",
            """{"Inputs":[{"Element":{"RowName":"Leather","DataTableName":"D_ItemsStatic"},"Count":2}]}"""));
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Recipe(vm).Fields.Single(f => f.Label == "Needs Stone").ValueText = "3";

        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);

        Assert.Equal("2", Recipe(vm).Fields.Single(f => f.Label == "Needs Leather").ValueText);
        Assert.Contains("1 typed change(s)", vm.StatusMessage);
    }

    /// <summary>Regression: a bench added here was lost on another Save to, and the taken-out one left the recipe with none.</summary>
    [Fact]
    public void AnAddedBench_ComesAlongToAnotherSaveTo()
    {
        WriteEnvirosuitRecipe();
        WriteBenches();
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Recipe(vm).BenchToAdd = Recipe(vm).BenchChoices!.Single(c => c.Name == "Fabricator");
        Recipe(vm).AddBenchCommand.Execute(null);
        Recipe(vm).Fields.Single(f => f.Label == "Made at").DropCommand.Execute(null);

        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);

        Assert.Single(Recipe(vm).Fields, f => f.ValueText == "Fabricator" && !f.IsDropped);
        Assert.True(Recipe(vm).Fields.Single(f => f.Label == "Made at").IsDropped);
        vm.SaveCommand.Execute(null);
        Assert.Equal(["Fabricator"], SavedList(mine.FolderName, "RecipeSets").Select(e => e!["RowName"]!.GetValue<string>()));
    }

    /// <summary>Regression: a bench taken out was carried by position onto the only bench another mod's recipe has, leaving it made nowhere.</summary>
    [Fact]
    public void ATakenOutBench_NeverLeavesARecipeWithNone()
    {
        Write("Crafting/D_ProcessorRecipes.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Envirosuit_Recipe","RecipeSets":[{"RowName":"Character","DataTableName":"D_RecipeSets"},{"RowName":"Fabricator","DataTableName":"D_RecipeSets"}],
               "Inputs":[{"Element":{"RowName":"Stone","DataTableName":"D_ItemsStatic"},"Count":4}],
               "Outputs":[{"Element":{"RowName":"Envirosuit","DataTableName":"D_ItemTemplate"},"Count":1}]}
            ]}
            """);
        WriteBenches();
        var oneBench = _library.CreateBlankMod("One bench", "Me");
        WriteIntoMod(oneBench.FolderName, ModRow("Crafting-D_ProcessorRecipes.json", "Envirosuit_Recipe",
            """{"RecipeSets":[{"RowName":"Character","DataTableName":"D_RecipeSets"}]}"""));
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Recipe(vm).Fields.Single(f => f.Field.Path.Key == "RecipeSets/0/RowName").DropCommand.Execute(null);

        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == oneBench.FolderName);

        Assert.False(Recipe(vm).Fields.Single(f => f.Field.Path.Key == "RecipeSets/0/RowName").IsDropped);
        Assert.Contains("1 typed change(s)", vm.StatusMessage);
    }

    /// <summary>Regression: a link the Save-to mod points at a row it adds itself showed "Pick one from the list." untouched and blocked every Save.</summary>
    [Fact]
    public void ALinkToTheModsOwnRow_DoesntBlockSaving()
    {
        WriteDeployableStone();
        var mine = _library.CreateBlankMod("Mine", "Me");
        WriteIntoMod(mine.FolderName,
            ModRow("Items-D_ItemTemplate.json", "Moldy_Stone", """{"ItemStaticData":{"RowName":"Stone"}}"""),
            ModRow("Traits-D_Decayable.json", "Decay_Stone", """{"SpoiledItem":{"RowName":"Moldy_Stone"}}"""));
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = Item(vm, "Stone");

        var spoils = vm.Sections.Single(s => s.Section.Key == "Decayable").Fields.Single(f => f.Label == "Spoils into");
        Assert.Equal("Moldy_Stone", spoils.ValueText);
        Assert.Null(spoils.Error);

        Weight(vm).ValueText = "5";
        vm.SaveCommand.Execute(null);
        Assert.StartsWith("Saved", vm.StatusMessage);
    }

    /// <summary>Regression: the Stack size chip doubled the table's stack size of 1 on items that don't stack (tools, armour), making them stack.</summary>
    [Fact]
    public void StackSize_LeavesOutItemsThatDontStack()
    {
        var vm = Open();

        vm.PickShortcutCommand.Execute(BulkEdit.Shortcuts.Single(s => s.Label == "Stack size"));

        Assert.Empty(vm.BulkRows);
        Assert.Contains("stack size of 1", vm.BulkSummary);
    }

    /// <summary>An item with a stack size of its own is still doubled; Set changes the ones that don't stack too.</summary>
    [Fact]
    public void StackSize_DoublesARealStack_AndSetStillReachesTheRest()
    {
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0,"MaxStack":1},"Rows":[
              {"Name":"Item_Envirosuit","Weight":100},
              {"Name":"Item_Stone","Weight":10,"MaxStack":50}
            ]}
            """);
        var vm = Open();

        vm.PickShortcutCommand.Execute(BulkEdit.Shortcuts.Single(s => s.Label == "Stack size"));

        Assert.Equal(["Stone: 50 → 100"], vm.BulkRows.Select(r => $"{r.Item.Name}: {r.CurrentText} → {r.NewText}"));
        Assert.Contains("1 item(s) that don't stack were left out", vm.BulkSummary);

        vm.SelectedBulkOperation = BulkEdit.Operations.Single(o => o.Operation == BulkOperation.Set);
        vm.BulkValueText = "5";
        Assert.Equal(["Envirosuit", "Stone"], vm.BulkRows.Select(r => r.Item.Name));
    }

    /// <summary>Regression: ticking another item reset a preview row typed over, though it's kept until another value is picked.</summary>
    [Fact]
    public void ATypedOverRow_SurvivesTickingAnotherItem()
    {
        var vm = Open();
        Item(vm, "Envirosuit").IsTicked = true;
        vm.OpenBulkCommand.Execute(null);
        PickBulkField(vm, "Itemable", "Weight");
        vm.MultiplyByCommand.Execute("2");
        vm.BulkRows.Single(r => r.Item.Name == "Envirosuit").NewText = "7";

        Item(vm, "Stone").IsTicked = true;

        Assert.Equal("7", vm.BulkRows.Single(r => r.Item.Name == "Envirosuit").NewText);
    }

    /// <summary>Regression: numbers typed over a row moved onto other ingredients when the row's values changed.</summary>
    [Fact]
    public void ATypedOverRow_IsDroppedWhenItsValuesChange()
    {
        WriteEnvirosuitRecipe();
        var vm = Open();
        Item(vm, "Envirosuit").IsTicked = true;
        vm.OpenBulkCommand.Execute(null);
        PickBulkField(vm, GameItemEditor.RecipeSection, "Inputs/*/Count");
        vm.SelectedBulkOperation = BulkEdit.Operations.Single(o => o.Operation == BulkOperation.Set);
        vm.BulkValueText = "2";
        Assert.Equal("4", vm.BulkRows.Single().CurrentText); // only Stone changes
        vm.BulkRows.Single().NewText = "3";

        vm.BulkValueText = "1"; // now Stone and Leather change

        var row = vm.BulkRows.Single();
        Assert.Equal("4, 2", row.CurrentText);
        Assert.Equal(row.ComputedText, row.NewText);
        Assert.Null(row.Error);
    }

    /// <summary>Two items changing one shared row "for all" to different values: Save refuses, naming both, instead of the later one silently winning.</summary>
    [Fact]
    public void ASharedRowSetToTwoValues_IsRefused_NamingBothItems()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        var decay = vm.Sections.Single(s => s.Section.Key == "Decayable");
        decay.ChangeForAll = true;
        decay.Fields.Single().ValueText = "600";
        vm.SelectedItem = Item(vm, "Stone");
        decay = vm.Sections.Single(s => s.Section.Key == "Decayable");
        decay.ChangeForAll = true;
        decay.Fields.Single().ValueText = "900";
        vm.NewModName = "Slow decay";

        vm.SaveCommand.Execute(null);

        Assert.Empty(_library.GetAll());
        Assert.StartsWith("Not saved", vm.StatusMessage);
        Assert.Contains("Xigo Envirosuit", vm.StatusMessage);
        Assert.Contains("Stone", vm.StatusMessage);
        Assert.Contains("600", vm.StatusMessage);
        Assert.Contains("900", vm.StatusMessage);

        decay.Fields.Single().ValueText = "600"; // the same value for both is fine
        vm.SaveCommand.Execute(null);
        Assert.StartsWith("Saved", vm.StatusMessage);
    }

    /// <summary>Taking out every ingredient saves (a free craft by hand can be on purpose) but warns when a bench makes it: a machine never starts a recipe with no inputs.</summary>
    [Fact]
    public void TakingOutEveryIngredient_SavesButWarnsForABench()
    {
        WriteEnvirosuitRecipe();
        WriteBenches();
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Recipe(vm).BenchToAdd = Recipe(vm).BenchChoices!.Single(c => c.Name == "Fabricator");
        Recipe(vm).AddBenchCommand.Execute(null);
        Recipe(vm).Fields.Single(f => f.Label == "Needs Stone").DropCommand.Execute(null);
        Recipe(vm).Fields.Single(f => f.Label == "Needs Leather").DropCommand.Execute(null);
        vm.NewModName = "Free at the bench";
        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        Assert.Contains("Xigo Envirosuit's recipe takes no ingredients", vm.StatusMessage);
        Assert.Empty(SavedList(Assert.Single(_library.GetAll()).FolderName, "Inputs"));

        vm.SelectedTarget = vm.Targets[0]; // a new mod: the game's recipe, made by hand
        Recipe(vm).Fields.Single(f => f.Label == "Needs Stone").DropCommand.Execute(null);
        Recipe(vm).Fields.Single(f => f.Label == "Needs Leather").DropCommand.Execute(null);
        vm.NewModName = "Free by hand";
        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        Assert.DoesNotContain("no ingredients", vm.StatusMessage); // a free craft by hand can be on purpose
    }

    /// <summary>Regression: the × on a waiting item's last change emptied its group in Your changes, but the item kept its pencil in the list.</summary>
    [Fact]
    public void UndoingAWaitingItemsLastChange_TakesItsPencilAway()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.SelectedItem = Item(vm, "Stone");
        Assert.True(Item(vm, "Envirosuit").HasUnsavedChanges);

        vm.PendingGroups.Single(g => g.ItemName == "Envirosuit").Changes.Single().Field.UndoChangeCommand.Execute(null);

        Assert.Empty(vm.PendingGroups);
        Assert.False(Item(vm, "Envirosuit").HasUnsavedChanges);
    }

    /// <summary>Regression: a table that couldn't be read while the page reloaded after Refresh game data threw out of the message, so Settings said "Refresh failed" and later listeners never heard of the new data.</summary>
    [Fact]
    public void RefreshGameData_WithATableThatCantBeRead_SaysSo_InsteadOfThrowing()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        Write("Items/D_ItemsStatic.json", "{ not json");

        Assert.Null(Record.Exception(() => WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage())));

        Assert.StartsWith("Couldn't read the game's items", vm.NoDataMessage);
    }

    /// <summary>Regression: the first load only caught a few kinds of error, so a table that isn't a table threw out of the page's async load instead of saying so.</summary>
    [Fact]
    public async Task TheFirstLoad_ATableThatIsntATable_SaysSo()
    {
        Write("Items/D_ItemsStatic.json", "[]");
        var vm = new GameEditorViewModel(_library, DataFolder);

        await vm.EnsureLoadedAsync();

        Assert.StartsWith("Couldn't read the game's items", vm.NoDataMessage);
    }

    /// <summary>Regression: a mod name Windows can't use as a file name failed with "EXMOD field 'fileName' must be a simple name, not a path".</summary>
    [Fact]
    public void ANewModNameWithCharactersWindowsCantUse_IsRefusedInPlainWords()
    {
        var vm = Open();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.NewModName = "Bread: better";

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("A mod name can't contain", vm.StatusMessage);
        Assert.Contains("Change the name, then Save.", vm.StatusMessage);
        Assert.Empty(_library.GetAll());
        Assert.Equal(1, vm.PendingCount); // nothing typed is lost
    }

    /// <summary>Regression: "Open in Editor" from a downloaded mod kept Save to as it was without a word, as if the values shown were that mod's.</summary>
    [Fact]
    public void OpenFromAModThatIsntYours_KeepsSaveTo_AndSaysWhy()
    {
        var nexus = ImportNexusMod();
        var vm = Open();

        WeakReferenceMessenger.Default.Send(new OpenGameItemMessage("Envirosuit", nexus));

        Assert.Equal("Envirosuit", vm.SelectedItem?.Name);
        Assert.True(vm.IsNewTarget);
        Assert.Contains("'From Nexus' isn't one of your own mods", vm.StatusMessage);
        Assert.Contains("Save makes a new mod", vm.StatusMessage);
    }

    /// <summary>The search runs once the typing stops, not on every key (each pass goes through every item).</summary>
    [Fact]
    public void TheSearch_WaitsForTheTypingToStop()
    {
        var vm = Open();

        vm.FilterText = "rock";

        Assert.Equal(["Envirosuit", "Stone"], Names(vm));
        vm.FlushFilter();
        Assert.Equal(["Stone"], Names(vm));
    }

    /// <summary>
    /// Regression: ↺ (or emptying the box) on an ingredient only the Save-to mod adds left it in the
    /// recipe with no amount (or "Count": null when the list was written whole), hidden on the card
    /// while the game still got it. Back to the game's means out of the recipe.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResettingAnIngredientTheModAdds_TakesItOut(bool emptyTheBox, bool alsoTakeOutLeather)
    {
        WriteEnvirosuitRecipe();
        var mine = _library.CreateBlankMod("Mine", "Me");
        WriteIntoMod(mine.FolderName, ModRow("Crafting-D_ProcessorRecipes.json", "Envirosuit_Recipe", """
            {"Inputs":[{"Element":{"RowName":"Stone","DataTableName":"D_ItemsStatic"},"Count":4},
                       {"Element":{"RowName":"Leather","DataTableName":"D_ItemsStatic"},"Count":2},
                       {"Element":{"RowName":"Fiber","DataTableName":"D_ItemsStatic"},"Count":3}]}
            """));
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = Item(vm, "Envirosuit");
        var fiber = Recipe(vm).Fields.Single(f => f.Label.Contains("Fiber", StringComparison.Ordinal));

        if (emptyTheBox)
        {
            fiber.ValueText = "";
        }
        else
        {
            fiber.UseGameValueCommand.Execute(null);
        }

        if (alsoTakeOutLeather)
        {
            Recipe(vm).Fields.Single(f => f.Label == "Needs Leather").DropCommand.Execute(null);
        }

        vm.SaveCommand.Execute(null);

        // Back to the game's whole list: the mod no longer writes the recipe's ingredients at all.
        var saved = Read(_library, mine.FolderName).Rows.SingleOrDefault(r => r.CurrentFile == "Crafting-D_ProcessorRecipes.json")?.FileItems.SingleOrDefault()?.Fields["Inputs"];
        var inputs = saved as JsonArray ?? JsonNode.Parse("""
            [{"Element":{"RowName":"Stone"},"Count":4},{"Element":{"RowName":"Leather"},"Count":2}]
            """)!.AsArray();
        Assert.Equal(alsoTakeOutLeather ? ["Stone:4"] : ["Stone:4", "Leather:2"], inputs.Select(e => $"{e!["Element"]!["RowName"]}:{e["Count"]}"));
        Assert.All(inputs, e => Assert.NotNull(e!["Count"]));
    }

    /// <summary>
    /// Regression: a link the Save-to mod writes as plain text ("Decayable": "Item_X") made reading
    /// the item throw, and the app said it had to close. Such a link now reads as no link, so both
    /// items show.
    /// </summary>
    [Fact]
    public void AValueOfTheWrongKindInTheMod_DoesntCloseTheApp()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        WriteIntoMod(mine.FolderName, ModRow("Items-D_ItemsStatic.json", "Envirosuit", """{"Decayable":"Item_X"}"""));
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);

        // Stone shares Envirosuit's decay row: counting who uses it reads the bad link.
        Assert.Null(Record.Exception(() => vm.SelectedItem = Item(vm, "Stone")));
        Assert.NotEmpty(vm.Sections);
        Assert.Null(Record.Exception(() => vm.SelectedItem = Item(vm, "Envirosuit")));
        Assert.NotEmpty(vm.Sections);
    }

    /// <summary>Regression: a waiting item rebuilt against a Save-to mod with a value of the wrong kind for it made the app say it had to close. The bad link reads as no link, so the typed change keeps waiting.</summary>
    [Fact]
    public void AWaitingItem_RebuiltAgainstAModWithAValueOfTheWrongKind_DoesntCloseTheApp()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        WriteIntoMod(mine.FolderName, ModRow("Items-D_ItemsStatic.json", "Envirosuit", """{"Decayable":"Item_X"}"""));
        var vm = Open();
        vm.SelectedItem = Item(vm, "Stone");
        Weight(vm).ValueText = "5";
        vm.SelectedItem = Item(vm, "Envirosuit"); // Stone's change waits

        Assert.Null(Record.Exception(() => vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName)));

        Assert.Equal(1, vm.EditedItemCount);
        Assert.NotEmpty(vm.Sections);
    }

    /// <summary>Regression: a Save whose item couldn't be applied to the Save-to mod (a value of the wrong kind written there meanwhile) made the app say it had to close. The mod changed after it was read, so Save now refuses in plain words before applying anything.</summary>
    [Fact]
    public void Save_IntoAModWithAValueOfTheWrongKind_SaysSo()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = Item(vm, "Stone");
        var decay = vm.Sections.Single(s => s.Section.Key == "Decayable");
        decay.ChangeForAll = true;
        decay.Fields.Single().ValueText = "600";
        WriteIntoMod(mine.FolderName, ModRow("Items-D_ItemsStatic.json", "Envirosuit", """{"Decayable":"Item_X"}"""));

        Assert.Null(Record.Exception(() => vm.SaveCommand.Execute(null)));
        Assert.StartsWith("Not saved", vm.StatusMessage);
    }

    /// <summary>
    /// Regression: a new mod's first save that failed after the mod was made (a locked Library_Meta
    /// file) left an empty mod in Library while Save to still said "A new mod", so Save again made
    /// a second one, &lt;Name&gt;_2.
    /// </summary>
    [Fact]
    public void AFailedFirstSave_LeavesNoEmptyMod_AndSaveAgainUsesTheName()
    {
        var locked = DispatchProxy.Create<ILibraryRepository, LockedMetaRepository>();
        ((LockedMetaRepository)(object)locked).Inner = _library;
        var vm = new GameEditorViewModel(locked, DataFolder);
        vm.EnsureLoaded();
        vm.SelectedItem = Item(vm, "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.NewModName = "Lighter";

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Couldn't save", vm.StatusMessage);
        Assert.Empty(_library.GetAll());
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "Extracted_Mods")));
        Assert.True(vm.SelectedTarget?.IsNew);
        Assert.Equal(1, vm.PendingCount); // still typed, to save again

        ((LockedMetaRepository)(object)locked).Locked = false;
        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        Assert.Equal("Lighter", Assert.Single(_library.GetAll()).FolderName);
    }

    /// <summary>The Library, except that marking a mod edited fails as a locked Library_Meta file does, while <see cref="Locked"/>.</summary>
    public class LockedMetaRepository : DispatchProxy
    {
        internal ILibraryRepository Inner { get; set; } = null!;

        internal bool Locked { get; set; } = true;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Locked && targetMethod?.Name == nameof(ILibraryRepository.MarkLocallyEdited))
            {
                throw new IOException("Couldn't read the saved settings (the file is locked or unreadable right now) — nothing was changed; try again in a moment.");
            }

            try
            {
                return targetMethod!.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(inner);
                throw;
            }
        }
    }

    /// <summary>Regression: emptying the box of the last ingredient left (one the Save-to mod adds, the others taken out) saved a recipe with no inputs without the warning a machine-made recipe gets.</summary>
    [Fact]
    public void EmptyingTheLastIngredientsBox_WarnsLikeTakingItOut()
    {
        WriteEnvirosuitRecipe();
        WriteBenches();
        var mine = _library.CreateBlankMod("Mine", "Me");
        WriteIntoMod(mine.FolderName, ModRow("Crafting-D_ProcessorRecipes.json", "Envirosuit_Recipe", """
            {"Inputs":[{"Element":{"RowName":"Stone","DataTableName":"D_ItemsStatic"},"Count":4},
                       {"Element":{"RowName":"Leather","DataTableName":"D_ItemsStatic"},"Count":2},
                       {"Element":{"RowName":"Fiber","DataTableName":"D_ItemsStatic"},"Count":3}]}
            """));
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = Item(vm, "Envirosuit");
        Recipe(vm).BenchToAdd = Recipe(vm).BenchChoices!.Single(c => c.Name == "Fabricator");
        Recipe(vm).AddBenchCommand.Execute(null);
        Recipe(vm).Fields.Single(f => f.Label == "Needs Stone").DropCommand.Execute(null);
        Recipe(vm).Fields.Single(f => f.Label == "Needs Leather").DropCommand.Execute(null);
        Recipe(vm).Fields.Single(f => f.Label.Contains("Fiber", StringComparison.Ordinal)).ValueText = "";
        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        Assert.Empty(SavedList(mine.FolderName, "Inputs"));
        Assert.Contains("Xigo Envirosuit's recipe takes no ingredients", vm.StatusMessage);
    }
}
