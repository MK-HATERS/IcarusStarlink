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

/// <summary>The mod editor's checkboxes, link pickers and value choices, read from the game's own tables.</summary>
[Collection(MessengerCollection.Name)]
public sealed class EditorFieldKindsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FieldKinds_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public EditorFieldKindsTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"}},"Rows":[
              {"Name":"Canteen","Itemable":{"RowName":"Item_Canteen"}}
            ]}
            """);
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":0,"bHidden":false},"Rows":[
              {"Name":"Item_Canteen","Weight":10,"bHidden":false,"SnapBehaviour":"Snap"},
              {"Name":"Item_Waterskin","Weight":5,"SnapBehaviour":"Free"},
              {"Name":"Item_Bottle","SnapBehaviour":"Snap"}
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

    private ExmodEditorViewModel OpenMod()
    {
        var entry = _library.CreateBlankMod("Canteens", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemsStatic.json",
            FileItems = [new ExmodFileItem { Name = "Canteen", Fields = { ["Itemable"] = JsonNode.Parse("""{"RowName":"Item_Canteen"}""") } }],
        });
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            FileItems = [new ExmodFileItem { Name = "Item_Canteen", Fields = { ["bHidden"] = JsonValue.Create(true), ["SnapBehaviour"] = JsonValue.Create("Free") } }],
        });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        return new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache());
    }

    private static EditorFieldViewModel Field(ExmodEditorViewModel editor, string name) => editor.Fields.Single(f => f.FieldName == name);

    [Fact]
    public void ATrueFalseField_IsACheckbox()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Canteen");
        var hidden = Field(editor, "bHidden");
        Assert.Equal(EditorFieldKind.Bool, hidden.Kind);
        Assert.True(hidden.BoolValue);

        hidden.BoolValue = false;

        Assert.Equal("false", hidden.ValueText);
        Assert.False(hidden.BoolValue);
        Assert.False(hidden.IsChanged); // the game's own value again
    }

    [Fact]
    public void ALinkField_OffersTheTargetTablesRows_AndRefusesARowThatIsntThere()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Canteen");
        var link = Field(editor, "Itemable");
        Assert.Equal(EditorFieldKind.Link, link.Kind);
        Assert.Equal("D_Itemable", link.LinkTable);
        Assert.Equal(["None", "Item_Bottle", "Item_Canteen", "Item_Waterskin"], link.Options);
        Assert.Equal("Item_Canteen", link.SelectedOption);

        link.SelectedOption = "Item_Flask";

        Assert.Contains("has no row 'Item_Flask'", link.ParseError);
        Assert.Equal("Item_Canteen", JsonNode.Parse(link.ValueText)!["RowName"]!.GetValue<string>());

        link.SelectedOption = "item_waterskin"; // typed in another case: written in the game's spelling

        Assert.Null(link.ParseError);
        Assert.Equal("Item_Waterskin", JsonNode.Parse(link.ValueText)!["RowName"]!.GetValue<string>());
        Assert.Equal("Item_Waterskin", link.SelectedOption);
    }

    private ExmodEditorViewModel OpenArmourMod()
    {
        Write("Traits/D_Armour.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"IO_Undersuit","ArmourStats":{"(Value=\"BaseOxygenSlots_+\")":1}}]}""");
        var entry = _library.CreateBlankMod("Suits", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Armour.json",
            FileItems = [new ExmodFileItem { Name = "IO_Undersuit", Fields = { ["ArmourStats"] = JsonNode.Parse("""{"(Value=\"BaseOxygenSlots_+\")":2}""") } }],
        });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        var editor = new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache());
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "IO_Undersuit");
        return editor;
    }

    /// <summary>Stats are whole numbers in the game (all 15,333 stat values in its tables are): a stat row and the stats JSON box both refuse 5.5.</summary>
    [Fact]
    public void Stats_NeedWholeNumbers_InEveryPlaceTheyCanBeTyped()
    {
        var editor = OpenArmourMod();
        var stats = Field(editor, "ArmourStats");
        Assert.Equal(EditorFieldKind.Stats, stats.Kind);
        var oxygen = stats.Stats.Single();

        oxygen.ValueText = "2.5";
        Assert.Contains("whole number", oxygen.Error);
        Assert.Equal(2, JsonNode.Parse(stats.ValueText)!["(Value=\"BaseOxygenSlots_+\")"]!.GetValue<long>());

        oxygen.ValueText = "3";
        Assert.Null(oxygen.Error);
        Assert.Equal(3, JsonNode.Parse(stats.ValueText)!["(Value=\"BaseOxygenSlots_+\")"]!.GetValue<long>());

        stats.ValueText = """{"(Value=\"BaseOxygenSlots_+\")": 4.5}""";
        Assert.Contains("whole numbers", stats.ParseError);
    }

    [Fact]
    public void ATextFieldWithAFewGameValues_IsAChoice()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Canteen");
        var snap = Field(editor, "SnapBehaviour");
        Assert.Equal(EditorFieldKind.Choice, snap.Kind);
        Assert.Equal(["Free", "Snap"], snap.Options);
        Assert.Equal("Free", snap.SelectedOption);

        snap.SelectedOption = "Wobble";
        Assert.Equal("Pick one of the values in the list.", snap.ParseError);
        Assert.Equal("Free", JsonNode.Parse(snap.ValueText)!.GetValue<string>());

        snap.SelectedOption = "snap";
        Assert.Null(snap.ParseError);
        Assert.Equal("Snap", JsonNode.Parse(snap.ValueText)!.GetValue<string>());
    }
}
