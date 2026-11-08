using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class GameEditorViewModelTests
{
    /// <summary>A paid DLC never holds an item back: a DLC-only item is listed, opens, and takes a saved value, and its gate stays as the game has it.</summary>
    [Fact]
    public void ADlcItem_IsListed_Opens_AndTakesASavedValue()
    {
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"}},"Rows":[
              {"Name":"Envirosuit","Itemable":{"RowName":"Item_Envirosuit"}},
              {"Name":"Hunter_Suit","Itemable":{"RowName":"Item_Hunter_Suit"},"Metadata":{"RequiredFeatureLevel":{"RowName":"GreatHunts"}}}
            ]}
            """);
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0},"Rows":[
              {"Name":"Item_Envirosuit","Weight":100},
              {"Name":"Item_Hunter_Suit","Weight":80}
            ]}
            """);
        var vm = Open();

        var hunter = Assert.Single(vm.FilteredItems, i => i.Name == "Hunter_Suit");
        vm.SelectedItem = hunter;
        Assert.True(vm.HasItem);
        Weight(vm).ValueText = "40";
        vm.NewModName = "Light Hunter";
        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        var entry = Assert.Single(_library.GetAll());
        var item = Read(_library, entry.FolderName).Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single();
        Assert.Equal("Item_Hunter_Suit", item.Name);
        Assert.Equal(40, item.Fields["Weight"]!.GetValue<int>());
    }

    /// <summary>Regression: a new mod saved here kept the blank mod's search words, so the Library search didn't find it by the items it changes.</summary>
    [Fact]
    public void Save_ToANewMod_IsFoundByItsItemsInTheLibrarySearch()
    {
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";
        vm.NewModName = "Lighter Suit";

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        Assert.Contains(_library.Search("Item_Envirosuit"), e => e.Name == "Lighter Suit");
    }

    /// <summary>Regression: a typed value on a row a Scale rule covers silently put the row on the rule's skip list; Save now says so.</summary>
    [Fact]
    public void Save_OnARowAScaleRuleSets_SaysTheRuleNowSkipsIt()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var folder = _library.GetFolderPath(mine.FolderName);
        var package = ExmodFolder.Read(folder).Package;
        package.ScaleRules.Add(new ScaleRule { Label = "Lighter", CurrentFile = "Traits-D_Itemable.json", Field = "Weight", Value = 0.5 });
        ExmodFolder.Write(folder, new ExmodPackageContents(package, []));
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "40";

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        Assert.Contains("Item_Envirosuit", Read(_library, mine.FolderName).ScaleRules.Single().ExceptRows);
        Assert.Contains("1 value(s) a Scale rule sets now stay as you typed them", vm.StatusMessage);
    }

    /// <summary>The repair needs of an item carry the note from the game's code (a hammer repair costs nothing); other durability values don't.</summary>
    [Fact]
    public void RepairNeeds_ShowTheNoteFromTheGamesCode_OtherFieldsDont()
    {
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"},"Durable":{"RowName":"None","DataTableName":"D_Durable"}},"Rows":[
              {"Name":"Envirosuit","Itemable":{"RowName":"Item_Envirosuit"},"Durable":{"RowName":"Durable_Suit"}},
              {"Name":"Stone","Itemable":{"RowName":"Item_Stone"}}
            ]}
            """);
        Write("Traits/D_Durable.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Durable_Suit","Max_Durability":500,"ItemsForRepair":[{"Item":{"RowName":"Stone","DataTableName":"D_ItemsStatic"},"Amount":2}]}
            ]}
            """);
        var vm = Open();
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");

        var durable = vm.Sections.Single(s => s.Section.Key == "Durable");
        var repair = durable.Fields.Single(f => f.Field.Path.Top == "ItemsForRepair");
        var max = durable.Fields.Single(f => f.Field.Path.Top == "Max_Durability");
        Assert.Equal(KnownGameBehaviour.HammerRepairs.Note, repair.GameCodeNote);
        Assert.Null(max.GameCodeNote);
    }
}
