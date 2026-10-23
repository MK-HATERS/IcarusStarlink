using System.IO;
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
public sealed class GameEditorViewModelTests : IDisposable
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
              {"Name":"Item_Envirosuit","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-DisplayName\", \"Xigo Envirosuit\")","Weight":100},
              {"Name":"Item_Stone","Weight":10}
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

    /// <summary>A review finding: clicking another item threw away everything typed, without a word.</summary>
    [Fact]
    public void SwitchingItem_WithTypedChanges_AsksFirst_AndStaysWhenTheAnswerIsNo()
    {
        var vm = Open();
        var asked = 0;
        vm.ConfirmDiscard = _ => { asked++; return false; };
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";

        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Stone");

        Assert.Equal(1, asked);
        Assert.Equal("Envirosuit", vm.SelectedItem?.Name);
        Assert.Equal("50", Weight(vm).ValueText);

        vm.ConfirmDiscard = _ => true;
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Stone");
        Assert.Equal("10", Weight(vm).ValueText);
    }

    /// <summary>A review finding: changing "Save to" with typed changes showed one mod's values and saved into another.</summary>
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

    /// <summary>A review finding: every Library change rebuilt the targets and reloaded the item, dropping a stat added here and not saved yet.</summary>
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

    /// <summary>A review finding: ↺ on a stat the target mod adds emptied the box ("Needs a number") and blocked saving; it now takes the stat out.</summary>
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

    /// <summary>A review finding: "Open in Editor" from a mod kept the page's own target, so it showed (and saved) the game's values.</summary>
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

    /// <summary>A review finding: Refresh game data reloaded the item and threw away every typed value.</summary>
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
}
