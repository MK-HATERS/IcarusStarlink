using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The mod editor's row commands, unsaved-work answer and game-data handling.</summary>
[Collection(MessengerCollection.Name)]
public sealed class ExmodEditorViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ExmodEditor_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public ExmodEditorViewModelTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"}},"Rows":[
              {"Name":"Envirosuit","Itemable":{"RowName":"Item_Envirosuit"}}
            ]}
            """);
        Write("Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{"Weight":0},"Rows":[{"Name":"Item_Envirosuit","Weight":100},{"Name":"Item_Fiber","Weight":10}]}""");
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

    /// <summary>A mod that halves the Envirosuit's weight.</summary>
    private ExmodEditorViewModel OpenMod()
    {
        var entry = _library.CreateBlankMod("Light suit", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            FileItems = [new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["Weight"] = JsonValue.Create(50) } }],
        });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        return new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache());
    }

    /// <summary>The item list says why it's empty, and the Fields view asks for a pick while nothing is picked.</summary>
    [Fact]
    public void EmptyStates_SearchWithNoMatch_AndNothingPicked()
    {
        var editor = OpenMod();
        Assert.Null(editor.ItemsEmptyText);
        Assert.False(editor.ShowPickItemPrompt); // the first item is picked on open

        editor.SelectedItem = null;
        Assert.True(editor.ShowPickItemPrompt);

        editor.FilterText = "no such item";
        editor.ApplyFilter();
        Assert.Equal("No items match the search.", editor.ItemsEmptyText);
        Assert.False(editor.ShowPickItemPrompt); // nothing listed to pick
    }

    [Fact]
    public void AddItem_TheSameRowInAnotherCase_IsRefused_NotAddedASecondTime()
    {
        var editor = OpenMod();
        editor.NewItemCurrentFile = "traits/d_itemable.json";
        editor.NewItemName = "item_envirosuit";

        editor.AddItemCommand.Execute(null);

        Assert.Single(editor.Items);
        Assert.Contains("already exists", editor.StatusMessage);
    }

    [Fact]
    public void AddItem_ToAFileTypedInAnotherCase_GoesIntoTheModsFile()
    {
        var editor = OpenMod();
        editor.NewItemCurrentFile = "traits/d_itemable.json";
        editor.NewItemName = "Item_Fiber";

        editor.AddItemCommand.Execute(null);

        Assert.Equal(2, editor.Items.Count);
        Assert.All(editor.Items, i => Assert.Equal("Traits-D_Itemable.json", i.CurrentFile));
    }

    /// <summary>The window's own Save prompt can't stop the app closing, so the main window's close prompt asks the editor first.</summary>
    [Fact]
    public void UnsavedChanges_AreReportedToTheAppsCloseCheck_UntilTheWindowCloses()
    {
        var editor = OpenMod();
        Assert.True(WeakReferenceMessenger.Default.IsRegistered<UnsavedEditsRequestMessage>(editor));

        editor.SelectedItem = editor.Items.Single();
        editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "25";

        Assert.True(editor.HasUnsavedChanges);
        Assert.Contains("an open mod editor's changes", AppBusyCheck.UnsavedEdits());

        editor.Detach();
        Assert.False(WeakReferenceMessenger.Default.IsRegistered<UnsavedEditsRequestMessage>(editor));
    }

    /// <summary>Since the raw JSON views went, a row copied in by mistake couldn't be taken out of the mod at all.</summary>
    [Fact]
    public void RemoveSelectedItems_TakesTheRowOutOfTheSavedMod_AndUndoPutsItBack()
    {
        var editor = OpenMod();
        editor.NewItemCurrentFile = "Traits/D_Itemable.json";
        editor.NewItemName = "Item_Fiber";
        editor.AddItemCommand.Execute(null);
        Assert.Equal(2, editor.Items.Count);

        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Fiber");
        editor.RemoveSelectedItemsCommand.Execute(null);

        Assert.Equal("Item_Envirosuit", Assert.Single(editor.Items).ItemName);
        editor.SaveCommand.Execute(null);
        var saved = ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_library.GetAll().Single().FolderName));
        Assert.Equal("Item_Envirosuit", Assert.Single(saved.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems).Name);

        editor.UndoCommand.Execute(null);
        Assert.Equal(2, editor.Items.Count);
    }

    /// <summary>The last row of a file goes with its file, so the mod isn't left with an empty table nobody can see.</summary>
    [Fact]
    public void RemoveSelectedItems_TheLastRowOfAFile_TakesTheFileOutToo()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single();

        editor.RemoveSelectedItemsCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        Assert.Empty(editor.Items);
        var saved = ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_library.GetAll().Single().FolderName));
        Assert.DoesNotContain(saved.Rows, r => r.CurrentFile == "Traits-D_Itemable.json");
    }

    /// <summary>A number typed with a comma ("2,5") didn't parse; Save said "Saved." over the old value and reset the box.</summary>
    [Fact]
    public void Save_WithATypedValueThatDidNotParse_IsRefused_InsteadOfSavingTheOldValue()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single();
        var weight = editor.Fields.Single(f => f.FieldName == "Weight");

        weight.ValueText = "2,5";
        editor.SaveCommand.Execute(null);

        Assert.StartsWith("Not saved", editor.StatusMessage);
        Assert.Equal("2,5", editor.Fields.Single(f => f.FieldName == "Weight").ValueText); // still on show to fix

        weight.ValueText = "25";
        editor.SaveCommand.Execute(null);
        Assert.StartsWith("Saved", editor.StatusMessage);
    }

    /// <summary>The editor kept the game tables it read for its whole life, so after Refresh game data it compared with the old game.</summary>
    [Fact]
    public void RefreshGameData_IsReadAgain()
    {
        var editor = OpenMod();
        Assert.Equal(1, editor.Items.Single().ChangeCount);
        editor.SelectedItem = editor.Items.Single();
        editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "40";

        Write("Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{"Weight":0},"Rows":[{"Name":"Item_Envirosuit","Weight":40}]}""");
        WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

        Assert.Equal(0, editor.Items.Single().ChangeCount);
        Assert.Equal("game: 40", editor.Fields.Single(f => f.FieldName == "Weight").BasePlainText);
        Assert.True(editor.HasUnsavedChanges); // the typed value is still there
    }
}
