using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The new-item wizard and the editor's reference check, on a real (temporary) library and game data folder.</summary>
[Collection(MessengerCollection.Name)]
public sealed class NewItemWizardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Wizard_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public NewItemWizardTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"},"Armour":{"RowName":"None","DataTableName":"D_Armour"},"Meshable":{"RowName":"None","DataTableName":"D_Meshable"}},"Rows":[
              {"Name":"Envirosuit","Meshable":{"RowName":"Mesh_Suit"},"Itemable":{"RowName":"Item_Envirosuit"},"Armour":{"RowName":"IO_Undersuit"}}
            ]}
            """);
        Write("Items/D_ItemTemplate.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Envirosuit","ItemStaticData":{"RowName":"Envirosuit"}}]}""");
        Write("Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Envirosuit","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-DisplayName\", \"Xigo Envirosuit\")","Weight":100}]}""");
        Write("Traits/D_Armour.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"IO_Undersuit","ArmourType":"Undersuit"}]}""");
        Write("Traits/D_Meshable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Mesh_Suit"}]}""");
        Write("Crafting/D_RecipeSets.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Crafting_Bench"}]}""");
        Write("MetaWorkshop/D_WorkshopItems.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Meta_Envirosuit_Basic","Item":{"RowName":"Envirosuit"},"ResearchCost":[{"Meta":{"RowName":"Credits"},"Amount":50}],"ReplicationCost":[{"Meta":{"RowName":"Credits"},"Amount":25}]}]}""");
        Write("Talents/D_Talents.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Workshop_Envirosuit","TalentTree":{"RowName":"Workshop_Envirosuits"},"Position":{"X":500,"Y":800}}]}""");
        Write("Talents/D_TalentTrees.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Workshop_Envirosuits","Archetype":{"RowName":"Workshop_Envirosuits"}}]}""");
        Write("Talents/D_TalentArchetypes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Workshop_Envirosuits","Model":{"RowName":"Workshop"}}]}""");
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

    private ExmodEditorViewModel Open(string folderName) => new(folderName, _library, DataFolder, new GameDataIndexCache());

    [Fact]
    public void Wizard_StartsFromTheTemplatePreset_AndTheIdFollowsTheName()
    {
        var wizard = new NewItemWizardViewModel(new GameTableIndex(DataFolder), null, ModTemplateContent.ItemPresetFor(ModTemplate.Envirosuit));

        Assert.Equal("Envirosuit", wizard.SelectedSource?.Name);
        Assert.True(wizard.IsWorkshop);
        Assert.Equal("Workshop_Envirosuits", wizard.SelectedTree);
        Assert.Equal(["Itemable", "Armour"], wizard.Links.Where(l => l.OwnCopy).Select(l => l.Field));

        wizard.DisplayName = "Dragon Suit!";
        Assert.Equal("Dragon_Suit", wizard.NewName);
        wizard.NewName = "Suit_Of_Dragons";
        wizard.DisplayName = "Dragon Suit";
        Assert.Equal("Suit_Of_Dragons", wizard.NewName);
    }

    /// <summary>"Any item in a workshop tab of your own": picking the item keeps the template's choice.</summary>
    [Fact]
    public void Wizard_TemplateChoiceSurvivesPickingTheItem()
    {
        var wizard = new NewItemWizardViewModel(new GameTableIndex(DataFolder), null, ModTemplateContent.ItemPresetFor(ModTemplate.WorkshopItem));
        Assert.Null(wizard.SelectedSource);

        wizard.SelectedSource = wizard.FilteredItems.Single(i => i.Name == "Envirosuit");

        Assert.True(wizard.IsWorkshop);
        Assert.True(wizard.UseNewTab);
    }

    [Fact]
    public void Wizard_SaysWhatsMissing_InsteadOfBuilding()
    {
        var wizard = new NewItemWizardViewModel(new GameTableIndex(DataFolder), null);

        Assert.False(wizard.TryCreate());
        Assert.Contains("Pick the game item", wizard.Error);

        wizard.SelectedSource = wizard.FilteredItems.Single();
        Assert.False(wizard.TryCreate());
        Assert.Contains("name", wizard.Error);

        wizard.DisplayName = "Envirosuit"; // the game's own row name
        Assert.False(wizard.TryCreate());
        Assert.Contains("already exists", wizard.Error);

        wizard.DisplayName = "Dragon Suit";
        wizard.ResearchCreditsText = "lots";
        Assert.False(wizard.TryCreate());
        Assert.Contains("whole numbers", wizard.Error);
    }

    [Fact]
    public void Editor_AddsTheWizardsRows_Undoably_AndSavesThemWhole()
    {
        var entry = _library.CreateBlankMod("Dragon", "Me", ModTemplate.Envirosuit);
        var editor = Open(entry.FolderName);
        editor.ShowWizardOverride = wizard =>
        {
            wizard.DisplayName = "Dragon Suit";
            return wizard.TryCreate();
        };

        editor.StartNewItemWizard(ModTemplateContent.ItemPresetFor(ModTemplate.Envirosuit));

        Assert.Equal("Dragon_Suit", editor.SelectedItem?.ItemName);
        Assert.Contains(editor.Items, i => i.CurrentFile == "Talents-D_Talents.json" && i.ItemName == "Workshop_Dragon_Suit");
        Assert.True(editor.CanUndo);

        editor.SaveCommand.Execute(null);
        var saved = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        // New rows aren't in the game, so Save keeps every field of them.
        Assert.Equal(100, saved.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single().Fields["Weight"]!.GetValue<int>());
        Assert.False(editor.ShowReferenceProblems);

        editor.UndoCommand.Execute(null);
        Assert.DoesNotContain(editor.Items, i => i.ItemName == "Dragon_Suit");
    }

    [Fact]
    public void Editor_CheckReferences_ListsProblems_AndClickingOneGoesToTheRow()
    {
        var entry = _library.CreateBlankMod("Broken", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemsStatic.json",
            FileItems = [new ExmodFileItem { Name = "Envirosuit", Fields = { ["Meshable"] = System.Text.Json.Nodes.JsonNode.Parse("""{"RowName":"Mesh_Envirosuit"}""") } }],
        });
        package.Rows.Add(new ExmodFileRow { CurrentFile = "Traits-D_Itemable.json", FileItems = [new ExmodFileItem { Name = "Item_Gone", Fields = { ["Weight"] = 1 } }] });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        var editor = Open(entry.FolderName);

        editor.CheckReferencesCommand.Execute(null);

        Assert.True(editor.ShowReferenceProblems);
        Assert.Equal(2, editor.ReferenceProblems.Count);
        Assert.Contains("Mesh_Envirosuit", editor.ReferenceProblems[0].Text);

        editor.GoToReferenceProblemCommand.Execute(editor.ReferenceProblems[1]);
        Assert.Equal("Item_Gone", editor.SelectedItem?.ItemName);

        // Save still saves, and says so.
        editor.SaveCommand.Execute(null);
        Assert.Contains("2 reference problem(s)", editor.StatusMessage);
    }

    [Fact]
    public void Editor_CheckReferences_OnACleanMod_SaysSo()
    {
        var entry = _library.CreateBlankMod("Clean", "Me", ModTemplate.Blank);
        var editor = Open(entry.FolderName);

        editor.CheckReferencesCommand.Execute(null);

        Assert.True(editor.ShowReferenceProblems);
        Assert.Empty(editor.ReferenceProblems);
        Assert.Contains("Every link", editor.ReferenceSummary);
    }

    /// <summary>A review finding: typing in the search box cleared the list, the ListBox pushed back a null selection, and the pick and every tick were lost.</summary>
    [Fact]
    public void Wizard_Searching_KeepsThePickedItemAndItsTicks()
    {
        var wizard = new NewItemWizardViewModel(new GameTableIndex(DataFolder), null);
        // What WPF's ListBox does when its items are cleared under the selection.
        wizard.FilteredItems.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                wizard.SelectedSource = null;
            }
        };
        wizard.SelectedSource = wizard.FilteredItems.Single(i => i.Name == "Envirosuit");
        wizard.Links.Single(l => l.Field == "Meshable").OwnCopy = true;

        wizard.FilterText = "nothing matches this";

        Assert.Equal("Envirosuit", wizard.SelectedSource?.Name);
        Assert.Contains(wizard.SelectedSource!, wizard.FilteredItems);
        Assert.True(wizard.Links.Single(l => l.Field == "Meshable").OwnCopy);
        Assert.True(wizard.HasSource);
    }

    [Fact]
    public void Wizard_WorkshopCosts_OnlyCountForAWorkshopItem()
    {
        var wizard = new NewItemWizardViewModel(new GameTableIndex(DataFolder), null);
        wizard.SelectedSource = wizard.FilteredItems.Single();
        wizard.DisplayName = "Dragon Suit";
        wizard.ResearchCreditsText = "lots";
        wizard.IsJustItem = true;

        Assert.True(wizard.TryCreate(), wizard.Error);
    }

    /// <summary>A review finding: any Undo rebuilt the rule rows from the snapshot, silently dropping rule edits not applied yet.</summary>
    [Fact]
    public void Editor_Undo_KeepsRuleEditsNotAppliedYet()
    {
        var entry = _library.CreateBlankMod("Rules", "Me", ModTemplate.Blank);
        var editor = Open(entry.FolderName);
        editor.ShowWizardOverride = wizard =>
        {
            wizard.DisplayName = "Dragon Suit";
            return wizard.TryCreate();
        };
        editor.StartNewItemWizard(null); // something to undo
        editor.AddScaleRuleCommand.Execute(null);
        editor.ScaleRuleRows[^1].Label = "My unapplied rule";

        editor.UndoCommand.Execute(null);

        Assert.Contains(editor.ScaleRuleRows, r => r.Label == "My unapplied rule");
    }
}
