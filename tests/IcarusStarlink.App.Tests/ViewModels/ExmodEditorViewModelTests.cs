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
public sealed partial class ExmodEditorViewModelTests : IDisposable
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

    /// <summary>Tested in game (Animal Talents 2.0): the game can't use rows a mod adds to D_CharacterGrowth, so a new one is refused with why; its own rows can still be changed.</summary>
    [Fact]
    public void AddItem_ANewRowInATableTheGameHasBuiltIn_IsRefused_ButItsOwnRowsAreFine()
    {
        Write("Character/D_CharacterGrowth.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Player","MaxLevel":60}]}""");
        var editor = OpenMod();
        editor.NewItemCurrentFile = "Character/D_CharacterGrowth.json";
        editor.NewItemName = "MkHaters_Wolf_Growth";

        editor.AddItemCommand.Execute(null);

        Assert.Single(editor.Items);
        Assert.StartsWith("Not added: The game can't use rows a mod adds", editor.StatusMessage);

        editor.NewItemName = "Player";
        editor.AddItemCommand.Execute(null);
        Assert.Equal(2, editor.Items.Count);
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

    /// <summary>A mod whose Envirosuit and Fiber rows both set MaxStack to 500.</summary>
    private ExmodEditorViewModel OpenModWithStacks()
    {
        var entry = _library.CreateBlankMod("Big stacks", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Insert(0, new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            FileItems =
            [
                new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["MaxStack"] = JsonValue.Create(500) } },
                new ExmodFileItem { Name = "Item_Fiber", Fields = { ["MaxStack"] = JsonValue.Create(500) } },
            ],
        });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        return new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache());
    }

    /// <summary>Regression: mass edit with a field name in another case ("maxstack") added a second field next to "MaxStack", so the saved mod held both.</summary>
    [Fact]
    public void MassEdit_WithTheFieldNameInAnotherCase_ChangesTheFieldThatIsThere()
    {
        var editor = OpenModWithStacks();
        editor.SetSelectedItemsForMassEdit([.. editor.Items]);
        editor.MassEditFieldName = "maxstack";
        editor.MassEditFieldValue = "1000";

        editor.ApplyMassEditCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        var saved = ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_library.GetAll().Single().FolderName));
        var items = saved.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems;
        Assert.Equal(2, items.Count);
        Assert.All(items, item =>
        {
            var key = Assert.Single(item.Fields.Keys, k => string.Equals(k, "MaxStack", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1000, item.Fields[key]!.GetValue<int>());
        });
    }

    /// <summary>Regression: "Type a field by hand" with "maxstack" added a second field next to "MaxStack".</summary>
    [Fact]
    public void AddField_WithANameThatIsThereInAnotherCase_IsRefused()
    {
        var editor = OpenModWithStacks();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Fiber");
        editor.NewFieldName = "maxstack";
        editor.NewFieldValue = "1000";

        editor.AddFieldCommand.Execute(null);

        Assert.Contains("already exists", editor.StatusMessage);
        Assert.False(editor.HasUnsavedChanges);
        Assert.Single(editor.Fields, f => string.Equals(f.FieldName, "MaxStack", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Regression: a value that didn't parse vanished without a word when another row was picked, and Save then said "Saved.".</summary>
    [Fact]
    public void ABadValue_LeftByPickingAnotherRow_IsSaidNotSilentlyDropped()
    {
        var editor = OpenMod();
        editor.NewItemCurrentFile = "Traits/D_Itemable.json";
        editor.NewItemName = "Item_Fiber";
        editor.AddItemCommand.Execute(null);
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Envirosuit");
        editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "2,5";

        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Fiber");

        Assert.Contains("wasn't kept", editor.StatusMessage);
    }

    /// <summary>Regression: after "Close Starlink anyway?", each open editor asked Save / Don't save / Cancel, and a Cancel there was ignored, so the edits were lost.</summary>
    [Fact]
    public void WhenStarlinkIsClosing_TheEditorAsksSaveOrDontSave_Only()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single();
        editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "25";
        editor.ClosingWithApp = true;
        editor.AskSaveOnClose = _ => throw new InvalidOperationException("no Cancel once Starlink is closing");
        string? asked = null;
        editor.AskSaveOnAppClose = message =>
        {
            asked = message;
            return true;
        };

        Assert.True(editor.ConfirmClose());

        Assert.Contains("Starlink is closing", asked);
        Assert.False(editor.HasUnsavedChanges);
        var saved = ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_library.GetAll().Single().FolderName));
        Assert.Equal(25, saved.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single().Fields["Weight"]!.GetValue<int>());
    }

    /// <summary>Every EXMOD ends with an EndOfMod marker; a table the editor added used to go after it.</summary>
    [Fact]
    public void ANewTable_GoesBeforeTheEndOfModMarker()
    {
        var entry = _library.CreateBlankMod("Marked", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            FileItems = [new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["Weight"] = JsonValue.Create(50) } }],
        });
        package.Rows.Add(new ExmodFileRow { CurrentFile = "EndOfMod" });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        var editor = new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache());
        editor.NewItemCurrentFile = "Items/D_ItemsStatic.json";
        editor.NewItemName = "Envirosuit";

        editor.AddItemCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        var saved = ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_library.GetAll().Single().FolderName));
        var table = saved.Rows.FindIndex(r => r.CurrentFile == "Items-D_ItemsStatic.json");
        var end = saved.Rows.FindIndex(r => r.CurrentFile == "EndOfMod");
        Assert.True(table >= 0 && end >= 0, "both rows are saved");
        Assert.True(table < end, $"the new table is at {table}, the EndOfMod marker at {end}");
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

    /// <summary>Another own mod holding the given Itemable rows, plus a table with no rows.</summary>
    private LibraryEntry OtherMod(params string[] itemableRows)
    {
        var entry = _library.CreateBlankMod("Other mod", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Insert(0, new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            FileItems = [.. itemableRows.Select(name => new ExmodFileItem { Name = name, Fields = { ["Weight"] = JsonValue.Create(5) } })],
        });
        package.Rows.Insert(1, new ExmodFileRow { CurrentFile = "Items-D_ItemsStatic.json" });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        return _library.GetAll().Single(e => e.FolderName == entry.FolderName);
    }

    /// <summary>Regression: "Nothing new to merge" still left an Undo step that changed nothing (and wiped Redo), and added the other mod's empty tables.</summary>
    [Fact]
    public void MergingAModWithNothingNew_LeavesNoUndoStep()
    {
        var editor = OpenMod();
        var other = OtherMod("Item_Envirosuit");
        editor.PickModToMerge = _ => other;

        editor.MergeExistingModCommand.Execute(null);

        Assert.StartsWith("Nothing new to merge", editor.StatusMessage);
        Assert.False(editor.CanUndo);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public void MergingAMod_AddsItsNewRows_AsOneUndoStep()
    {
        var editor = OpenMod();
        var other = OtherMod("Item_Envirosuit", "Item_Fiber");
        editor.PickModToMerge = _ => other;

        editor.MergeExistingModCommand.Execute(null);

        Assert.Equal("Merged 1 item(s) from 'Other mod'.", editor.StatusMessage);
        Assert.Equal(["Item_Envirosuit", "Item_Fiber"], editor.Items.Select(i => i.ItemName).Order());
        editor.UndoCommand.Execute(null);
        Assert.Equal("Item_Envirosuit", Assert.Single(editor.Items).ItemName);
    }

    /// <summary>Regression: the editor registered on and sent through the shared messenger even when given its own.</summary>
    [Fact]
    public void TheEditor_UsesTheMessengerItIsGiven()
    {
        var entry = _library.CreateBlankMod("Light suit", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Insert(0, new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            FileItems = [new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["Weight"] = JsonValue.Create(50) } }],
        });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        var messenger = new WeakReferenceMessenger();
        var editor = new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache(), messenger);

        Assert.True(messenger.IsRegistered<UnsavedEditsRequestMessage>(editor));
        Assert.False(WeakReferenceMessenger.Default.IsRegistered<UnsavedEditsRequestMessage>(editor));

        var listener = new object();
        var heardHere = 0;
        var heardShared = 0;
        messenger.Register<LibraryChangedMessage>(listener, (_, _) => heardHere++);
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(listener, (_, _) => heardShared++);
        try
        {
            editor.SelectedItem = editor.Items.Single();
            editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "25";
            editor.SaveCommand.Execute(null);

            Assert.StartsWith("Saved", editor.StatusMessage);
            Assert.Equal(1, heardHere);
            Assert.Equal(0, heardShared);

            editor.Detach();
            Assert.False(messenger.IsRegistered<UnsavedEditsRequestMessage>(editor));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(listener);
        }
    }
}
