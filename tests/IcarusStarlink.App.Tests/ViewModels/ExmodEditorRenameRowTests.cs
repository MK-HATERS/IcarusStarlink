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

/// <summary>More ▾ › Rename this row… (F2) in the mod editor, on a real (temporary) library and game data folder.</summary>
[Collection(MessengerCollection.Name)]
public sealed class ExmodEditorRenameRowTests : IDisposable
{
    private const string Itemable = "Traits-D_Itemable.json";
    private const string ItemsStatic = "Items-D_ItemsStatic.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "EditorRenameRow_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public ExmodEditorRenameRowTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"}},"Rows":[
              {"Name":"Fiber","Itemable":{"RowName":"Item_Fiber"}}
            ]}
            """);
        Write("Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{"Weight":0},"Rows":[{"Name":"Item_Fiber","Weight":10}]}""");
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

    /// <summary>A mod with a new item (its static row links its own Itemable) and a copy of the game's Item_Fiber.</summary>
    private (ExmodEditorViewModel Editor, string Folder) Open()
    {
        var entry = _library.CreateBlankMod("Rename", "Me", ModTemplate.Blank);
        var folder = _library.GetFolderPath(entry.FolderName);
        var package = ExmodFolder.Read(folder).Package;
        package.Rows.InsertRange(0,
        [
            new ExmodFileRow { CurrentFile = ItemsStatic, FileItems = [new ExmodFileItem { Name = "Dragon_Suit", Fields = new() { ["Itemable"] = JsonNode.Parse("""{"RowName":"Item_Dragon_Suit"}""") } }] },
            new ExmodFileRow
            {
                CurrentFile = Itemable,
                FileItems =
                [
                    new ExmodFileItem { Name = "Item_Dragon_Suit", Fields = new() { ["Weight"] = 5 } },
                    new ExmodFileItem { Name = "Item_Fiber", Fields = new() { ["Weight"] = 1 } },
                ],
            },
        ]);
        ExmodFolder.Write(folder, new ExmodPackageContents(package, []));
        var editor = new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache());
        return (editor, entry.FolderName);
    }

    private static void Select(ExmodEditorViewModel editor, string row) => editor.SelectedItem = editor.Items.Single(i => i.ItemName == row);

    private static string LinkOf(ExmodEditorViewModel editor)
    {
        Select(editor, "Dragon_Suit");
        return editor.Fields.Single(f => f.FieldName == "Itemable").SelectedOption;
    }

    [Fact]
    public void Rename_MovesTheLinks_SaysSo_AndUndoPutsBothBack()
    {
        var (editor, folder) = Open();
        Select(editor, "Item_Dragon_Suit");
        editor.AskNewRowName = (current, _) => current == "Item_Dragon_Suit" ? "Item_Wyvern_Suit" : null;

        editor.RenameRowCommand.Execute(null);

        Assert.Equal("Item_Wyvern_Suit", editor.SelectedItem?.ItemName);
        Assert.DoesNotContain(editor.Items, i => i.ItemName == "Item_Dragon_Suit");
        Assert.Contains("Renamed 'Item_Dragon_Suit' to 'Item_Wyvern_Suit' and pointed 1 link(s)", editor.StatusMessage);
        Assert.Equal("Item_Wyvern_Suit", LinkOf(editor));

        editor.SaveCommand.Execute(null);
        var saved = ExmodFolder.Read(_library.GetFolderPath(folder)).Package;
        Assert.Contains(saved.Rows.Single(r => r.CurrentFile == Itemable).FileItems, i => i.Name == "Item_Wyvern_Suit");

        editor.UndoCommand.Execute(null);

        Assert.Contains(editor.Items, i => i.ItemName == "Item_Dragon_Suit");
        Assert.DoesNotContain(editor.Items, i => i.ItemName == "Item_Wyvern_Suit");
        Assert.Equal("Item_Dragon_Suit", LinkOf(editor));
    }

    /// <summary>A copy of a game row changes that row by its name: no prompt, a reason, and nothing changes.</summary>
    [Fact]
    public void Rename_RefusesAGameRow_WithoutAsking()
    {
        var (editor, _) = Open();
        Select(editor, "Item_Fiber");
        editor.AskNewRowName = (_, _) => throw new Xunit.Sdk.XunitException("asked for a name for a game row");

        editor.RenameRowCommand.Execute(null);

        Assert.Contains("the game's own row", editor.StatusMessage);
        Assert.Contains("Duplicate", editor.StatusMessage);
        Assert.Contains(editor.Items, i => i.ItemName == "Item_Fiber");
        Assert.False(editor.CanUndo);
    }

    [Theory]
    [InlineData("item_fiber", "The game already has a row 'Item_Fiber'")]
    [InlineData("Dragon Suit", "letters, digits and _")]
    public void Rename_RefusesClashesAndBadNames_AndChangesNothing(string typed, string expected)
    {
        var (editor, _) = Open();
        Select(editor, "Item_Dragon_Suit");
        editor.AskNewRowName = (_, _) => typed;

        editor.RenameRowCommand.Execute(null);

        Assert.Contains("Not renamed", editor.StatusMessage);
        Assert.Contains(expected, editor.StatusMessage);
        Assert.Contains(editor.Items, i => i.ItemName == "Item_Dragon_Suit");
        Assert.Equal("Item_Dragon_Suit", LinkOf(editor));
        Assert.False(editor.CanUndo);
    }
}
