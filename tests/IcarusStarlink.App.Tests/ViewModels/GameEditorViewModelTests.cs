using System.IO;
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
}
