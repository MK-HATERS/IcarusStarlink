using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// The mod editor's Fields view (sections, "Only changed fields", ↺ back to the game's value, Add a
/// field by plain name) and its "Not in this mod yet" view, on a real (temporary) library.
/// </summary>
[Collection(MessengerCollection.Name)]
public sealed class ExmodEditorFieldsViewTests : IDisposable
{
    private const string Itemable = "Traits-D_Itemable.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "EditorFieldsView_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public ExmodEditorFieldsViewTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"}},"Rows":[
              {"Name":"Fiber","Itemable":{"RowName":"Item_Fiber"}}
            ]}
            """);
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0,"MaxStack":1,"bCanStack":false},"Rows":[
              {"Name":"Item_Fiber","Weight":10,"MaxStack":100,"DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Fiber-DisplayName\", \"Fiber\")"},
              {"Name":"Item_Stone","Weight":20,"MaxStack":50},
              {"Name":"Item_Wood","Weight":30,"MaxStack":40},
              {"Name":"Item_Stick","Weight":5,"MaxStack":20},
              {"Name":"Item_NewOre","Weight":40,"MaxStack":10},
              {"Name":"Item_Axe","Weight":800}
            ]}
            """);
        Write("Traits/D_Armour.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Leather_Chest","ArmourStats":{"(Value=\"BaseColdResistance_%\")":6,"(Value=\"BaseHeatResistance_%\")":4}}
            ]}
            """);
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

    private (ExmodEditorViewModel Editor, string Folder) Open(params ExmodFileRow[] rows)
    {
        var entry = _library.CreateBlankMod("Fields", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.InsertRange(0, rows);
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        return (new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache()), entry.FolderName);
    }

    private static ExmodFileItem Row(string name, params (string Field, JsonNode? Value)[] fields)
    {
        var item = new ExmodFileItem { Name = name };
        foreach (var (field, value) in fields)
        {
            item.Fields[field] = value;
        }

        return item;
    }

    /// <summary>A copied game row: one changed value among several the game already has.</summary>
    private (ExmodEditorViewModel Editor, string Folder) OpenCopiedFiber() => Open(new ExmodFileRow
    {
        CurrentFile = Itemable,
        FileItems =
        [
            Row("Item_Fiber",
                ("Weight", JsonValue.Create(10)),
                ("MaxStack", JsonValue.Create(1000)),
                ("bCanStack", JsonValue.Create(false)),
                ("DisplayName", JsonValue.Create("NSLOCTEXT(\"D_Itemable\", \"Item_Fiber-DisplayName\", \"Fiber\")"))),
        ],
    });

    private static EditorFieldViewModel Field(ExmodEditorViewModel editor, string name) => editor.Fields.Single(f => f.FieldName == name);

    [Fact]
    public void Fields_AreShownInSections_NamesFirst()
    {
        var (editor, _) = OpenCopiedFiber();

        Assert.Equal(["Names and text", "Numbers", "Numbers", "On or off"], editor.Fields.Select(f => f.Group));
        Assert.Equal(["DisplayName", "Weight", "MaxStack", "bCanStack"], editor.Fields.Select(f => f.FieldName));
        Assert.Equal("4 field(s), 1 differ from the game", editor.FieldsSummary);
    }

    [Fact]
    public void OnlyChangedFields_HidesTheOnesWithTheGamesValue_AndSaysWhyWhenNoneAreLeft()
    {
        var (editor, _) = OpenCopiedFiber();

        editor.ShowChangedFieldsOnly = true;

        Assert.Equal("MaxStack", Assert.Single(editor.Fields).FieldName);
        Assert.Equal("4 field(s), 1 differ from the game (showing 1)", editor.FieldsSummary);

        Field(editor, "MaxStack").UseGameValueCommand.Execute(null);
        editor.SelectedItem = null;
        editor.SelectedItem = editor.Items.Single();

        Assert.Empty(editor.Fields);
        Assert.Contains("No field differs from the game yet", editor.FieldsEmptyText);
    }

    /// <summary>↺ puts the game's value back in the box the user sees, writes it into the mod, and Save then drops the field.</summary>
    [Fact]
    public void UseGameValue_PutsTheGamesValueBack_AndSaveDropsTheField()
    {
        var (editor, folder) = OpenCopiedFiber();
        var maxStack = Field(editor, "MaxStack");
        Assert.True(maxStack.CanUseGameValue);
        var name = Field(editor, "DisplayName");
        name.PlainText = "Plant fibre";
        Assert.True(name.CanUseGameValue);

        maxStack.UseGameValueCommand.Execute(null);
        name.UseGameValueCommand.Execute(null);

        Assert.Equal("100", maxStack.ValueText);
        Assert.False(maxStack.CanUseGameValue);
        Assert.Equal("Fiber", name.PlainText);
        Assert.Equal(0, editor.Items.Single().ChangeCount);

        editor.SaveCommand.Execute(null);
        var saved = ExmodFolder.Read(_library.GetFolderPath(folder)).Package.Rows.Single(r => r.CurrentFile == Itemable).FileItems.Single().Fields;
        Assert.False(saved.ContainsKey("MaxStack"));
        Assert.False(saved.ContainsKey("DisplayName"));

        editor.UndoCommand.Execute(null);
        Assert.Equal("Plant fibre", Field(editor, "DisplayName").PlainText);
    }

    /// <summary>A stats field's rows are rebuilt too: a stat the mod dropped comes back, an added one goes.</summary>
    [Fact]
    public void UseGameValue_OnAStatsField_RebuildsItsStatRows()
    {
        var (editor, _) = Open(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Armour.json",
            FileItems = [Row("Leather_Chest", ("ArmourStats", new JsonObject { ["(Value=\"BaseColdResistance_%\")"] = 50, ["(Value=\"BaseMovementSpeed_+%\")"] = 5 }))],
        });
        var stats = Field(editor, "ArmourStats");
        Assert.Contains(stats.Stats, s => s.IsDropped);

        stats.UseGameValueCommand.Execute(null);

        Assert.Equal(2, stats.Stats.Count);
        Assert.All(stats.Stats, s => Assert.False(s.IsDropped || s.IsAdded));
        Assert.Equal("6", stats.Stats.Single(s => s.StatName == "BaseColdResistance_%").ValueText);
        Assert.False(stats.IsChanged);
    }

    /// <summary>Add a field: the fields the game's row has, by plain name, added with the game's value and kept in view.</summary>
    [Fact]
    public void AddAField_ListsTheGameRowsOtherFields_ByPlainName_AndAddsOneWithTheGamesValue()
    {
        var (editor, _) = Open(new ExmodFileRow { CurrentFile = Itemable, FileItems = [Row("Item_Fiber", ("MaxStack", JsonValue.Create(1000)))] });

        Assert.Equal(["bCanStack", "DisplayName", "Weight"], editor.MatchingAddableFields.Select(f => f.FieldName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(editor.MatchingAddableFields, f => f.Display == "Name in game  (DisplayName)");

        editor.AddFieldSearch = "weigh";
        var weight = Assert.Single(editor.MatchingAddableFields);
        Assert.Equal("game: 10", weight.GameText);

        editor.ShowChangedFieldsOnly = true;
        editor.SelectedAddableField = weight;
        editor.AddGameFieldCommand.Execute(null);

        Assert.Equal("10", Field(editor, "Weight").ValueText);
        Assert.False(Field(editor, "Weight").IsChanged);
        Assert.DoesNotContain(editor.MatchingAddableFields, f => f.FieldName == "Weight");
        Assert.Equal("", editor.AddFieldSearch);
        Assert.Equal(2, editor.AddableFieldCount);

        editor.UndoCommand.Execute(null);
        Assert.DoesNotContain(editor.Fields, f => f.FieldName == "Weight");
    }

    /// <summary>A row the mod adds itself (a new item) gets the table's default fields to add.</summary>
    [Fact]
    public void AddAField_OnANewRow_OffersTheTablesDefaults()
    {
        var (editor, _) = Open(new ExmodFileRow { CurrentFile = Itemable, FileItems = [Row("Item_MyThing", ("Weight", JsonValue.Create(1)))] });

        Assert.Equal(["bCanStack", "MaxStack"], editor.MatchingAddableFields.Select(f => f.FieldName).Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A stack mod: the game rows that stack and aren't in the mod, added with the mod's own ×10.</summary>
    [Fact]
    public void NotInModYet_ListsTheStackingRowsTheModMisses_AndAddsThemWithTheModsOwnChange()
    {
        var (editor, folder) = Open(new ExmodFileRow
        {
            CurrentFile = Itemable,
            FileItems = [Row("Item_Fiber", ("MaxStack", JsonValue.Create(1000))), Row("Item_Stone", ("MaxStack", JsonValue.Create(500))), Row("Item_Wood", ("MaxStack", JsonValue.Create(400)))],
        });

        editor.ViewMode = ExmodEditorViewMode.NotInMod;

        Assert.Equal(Itemable, editor.NotInModTable);
        Assert.Equal("MaxStack", editor.NotInModField);
        // Item_Axe inherits a stack of 1: it doesn't stack, so a stack mod doesn't want it.
        Assert.Equal(["Item_NewOre", "Item_Stick"], editor.NotInModRows.Select(r => r.RowName).Order());
        Assert.Equal("game: 20", editor.NotInModRows.Single(r => r.RowName == "Item_Stick").GameValueText);
        Assert.True(editor.HasNotInModChange);
        Assert.Contains("Stack size ×10", editor.NotInModChangeText);
        // The line under the list names the picked field (it went blank: "field" is a keyword inside a C# 14 property).
        Assert.Contains("Stack size", editor.NotInModAddHint);

        editor.PickAllNotInModCommand.Execute(null);
        editor.AddNotInModRowsCommand.Execute(null);

        Assert.Empty(editor.NotInModRows);
        Assert.Contains("Added 2 row(s)", editor.NotInModStatus);
        editor.SaveCommand.Execute(null);
        var saved = ExmodFolder.Read(_library.GetFolderPath(folder)).Package.Rows.Single(r => r.CurrentFile == Itemable).FileItems;
        Assert.Equal(200, saved.Single(i => i.Name == "Item_Stick").Fields["MaxStack"]!.GetValue<long>());
        Assert.Equal(100, saved.Single(i => i.Name == "Item_NewOre").Fields["MaxStack"]!.GetValue<long>());
        Assert.Single(saved.Single(i => i.Name == "Item_NewOre").Fields);

        editor.UndoCommand.Execute(null);
        Assert.Equal(3, editor.Items.Count);
    }

    [Fact]
    public void NotInModYet_AnyRow_AddsWholeGameRows_AndTheSearchNarrowsTheList()
    {
        var (editor, _) = Open(new ExmodFileRow { CurrentFile = Itemable, FileItems = [Row("Item_Fiber", ("Weight", JsonValue.Create(1)))] });
        editor.ViewMode = ExmodEditorViewMode.NotInMod;
        editor.NotInModField = ExmodEditorViewModel.AnyRowOption;
        Assert.Equal(5, editor.NotInModRows.Count);
        Assert.False(editor.HasNotInModChange);

        editor.NotInModSearch = "axe";
        var axe = Assert.Single(editor.NotInModRows);
        axe.IsPicked = true;
        editor.AddNotInModRowsCommand.Execute(null);

        var added = editor.Items.Single(i => i.ItemName == "Item_Axe");
        editor.SelectedItem = added;
        Assert.Equal("800", Field(editor, "Weight").ValueText);
        Assert.Contains("with the game's values", editor.NotInModStatus);
    }

    [Fact]
    public void NotInModYet_NothingTicked_SaysSo()
    {
        var (editor, _) = Open(new ExmodFileRow { CurrentFile = Itemable, FileItems = [Row("Item_Fiber", ("Weight", JsonValue.Create(1)))] });
        editor.ViewMode = ExmodEditorViewMode.NotInMod;

        editor.AddNotInModRowsCommand.Execute(null);

        Assert.Contains("Tick the rows", editor.NotInModStatus);
        Assert.False(editor.CanUndo);
    }
}
