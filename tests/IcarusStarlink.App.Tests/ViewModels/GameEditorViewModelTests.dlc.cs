using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>"Remove the DLC requirement": a DLC item's gate taken off in the user's own mod, and put back like any other change.</summary>
public sealed partial class GameEditorViewModelTests
{
    private const string Gate = """{"RequiredFeatureLevel":{"RowName":"GreatHunts"}}""";

    /// <summary>A Great Hunts suit whose gate sits on its D_ItemsStatic, Itemable and D_ItemTemplate rows, like the game's.</summary>
    private void WriteDlcSuit()
    {
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"}},"Rows":[
              {"Name":"Envirosuit","Itemable":{"RowName":"Item_Envirosuit"}},
              {"Name":"Hunter_Suit","Itemable":{"RowName":"Item_Hunter_Suit"},"Metadata":GATE},
              {"Name":"Fishing_Rod","Itemable":{"RowName":"Item_Fishing_Rod"},"Metadata":{"RequiredFeatureLevel":{"RowName":"Galileo"}}}
            ]}
            """.Replace("GATE", Gate));
        Write("Items/D_ItemTemplate.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Envirosuit","ItemStaticData":{"RowName":"Envirosuit"}},
              {"Name":"Hunter_Suit","ItemStaticData":{"RowName":"Hunter_Suit"},"Metadata":GATE}
            ]}
            """.Replace("GATE", Gate));
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0},"Rows":[
              {"Name":"Item_Envirosuit","Weight":100},
              {"Name":"Item_Hunter_Suit","Weight":80,"Metadata":GATE},
              {"Name":"Item_Fishing_Rod","Weight":5}
            ]}
            """.Replace("GATE", Gate));
        Write("Development/D_FeatureLevels.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"GreatHunts","DisplayName":"INVTEXT(\"Great Hunts\")"}]}""");
    }

    private static GameItemFieldViewModel? DlcChoice(GameEditorViewModel vm) =>
        vm.Sections.SingleOrDefault(s => s.Section.Key == GameItemEditor.DlcSection)?.Fields.Single();

    private static JsonNode? GateIn(ExmodPackage package, string file, string row) =>
        package.Rows.SingleOrDefault(r => r.CurrentFile == file)?.FileItems.SingleOrDefault(i => i.Name == row) is { } item
            ? item.Fields.TryGetValue("Metadata", out var metadata) ? metadata?["RequiredFeatureLevel"] ?? JsonValue.Create("removed") : null
            : null;

    /// <summary>A DLC item shows the choice by the DLC's name; ticking it and saving takes the gate off every row of the item that has one.</summary>
    [Fact]
    public void ADlcItem_RemoveTheDlcRequirement_SavesTheGateOffEveryRowOfTheItem()
    {
        WriteDlcSuit();
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Hunter_Suit");

        var choice = DlcChoice(vm);
        Assert.NotNull(choice);
        Assert.Equal("DLC: Great Hunts", vm.Sections.Single(s => s.Section.Key == GameItemEditor.DlcSection).Title);
        Assert.Equal("Remove the DLC requirement", choice.Label);
        Assert.True(choice.IsToggle);
        Assert.False(choice.IsOn);

        choice.IsOn = true;
        Assert.Equal(1, vm.PendingCount);
        vm.NewModName = "No DLC";
        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        var package = Read(_library, Assert.Single(_library.GetAll()).FolderName);
        // The Metadata field stays, without the gate: an empty Metadata is how the game's own ungated rows read.
        Assert.Equal("\"removed\"", GateIn(package, "Items-D_ItemsStatic.json", "Hunter_Suit")?.ToJsonString());
        Assert.Equal("\"removed\"", GateIn(package, "Traits-D_Itemable.json", "Item_Hunter_Suit")?.ToJsonString());
        Assert.Equal("\"removed\"", GateIn(package, "Items-D_ItemTemplate.json", "Hunter_Suit")?.ToJsonString());
        Assert.True(DlcChoice(vm)!.IsOn); // shown as the saved mod has it
    }

    /// <summary>Unticking it on a mod that took the gate off is a change like any other: Save takes it back out of the mod.</summary>
    [Fact]
    public void TheDlcRequirement_UntickedAndSaved_IsTakenBackOutOfTheMod()
    {
        WriteDlcSuit();
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Hunter_Suit");
        DlcChoice(vm)!.IsOn = true;
        vm.NewModName = "No DLC";
        vm.SaveCommand.Execute(null);
        var folder = Assert.Single(_library.GetAll()).FolderName;

        var again = Open();
        again.SelectedTarget = again.Targets.Single(t => t.FolderName == folder);
        again.SelectedItem = again.FilteredItems.Single(i => i.Name == "Hunter_Suit");
        var choice = DlcChoice(again)!;
        Assert.True(choice.IsOn);
        choice.IsOn = false;
        Assert.Equal(1, again.PendingCount);
        again.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", again.StatusMessage);
        var package = Read(_library, folder);
        Assert.DoesNotContain(package.Rows.SelectMany(r => r.FileItems), i => i.Fields.ContainsKey("Metadata"));
    }

    /// <summary>The × in Your changes takes the tick back before Save; nothing is written.</summary>
    [Fact]
    public void TheDlcRequirement_UndoneInYourChanges_LeavesNothingToSave()
    {
        WriteDlcSuit();
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Hunter_Suit");
        DlcChoice(vm)!.IsOn = true;

        var line = Assert.Single(Assert.Single(vm.PendingGroups).Changes);
        Assert.Equal("DLC: Great Hunts · Remove the DLC requirement: on", line.Text);
        line.Field.UndoChangeCommand.Execute(null);

        Assert.Equal(0, vm.PendingCount);
        Assert.False(DlcChoice(vm)!.IsOn);
    }

    /// <summary>Base-game items and free updates (Galileo is fishing) have no DLC to remove: no choice.</summary>
    [Fact]
    public void ABaseGameOrFreeUpdateItem_HasNoDlcChoice()
    {
        WriteDlcSuit();
        var vm = Open();

        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Assert.Null(DlcChoice(vm));
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Fishing_Rod");
        Assert.Null(DlcChoice(vm));
    }
}
