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
        editor.ViewMode = ExmodEditorViewMode.FullExmodJson;
        var rows = JsonNode.Parse(editor.FullExmodJsonText)!["Rows"]!.AsArray();
        Assert.Single(rows, r => string.Equals(r!["CurrentFile"]!.GetValue<string>(), "Traits-D_Itemable.json", StringComparison.OrdinalIgnoreCase));
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
