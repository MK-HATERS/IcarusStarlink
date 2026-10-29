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

/// <summary>The mod editor's plain Fields view, on the shape of a real mod (nik4kin's Larkwell envirosuit cheat).</summary>
[Collection(MessengerCollection.Name)]
public sealed class EditorPlainFieldsTests : IDisposable
{
    private const string Stats = """{"(Value=\"BaseUpgradeSlots_+\")":4,"(Value=\"BaseBackpackSlots_+\")":6,"(Value=\"BaseOxygenSlots_+\")":1}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "PlainFields_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public EditorPlainFieldsTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"},"Armour":{"RowName":"None","DataTableName":"D_Armour"}},"Rows":[
              {"Name":"Envirosuit_Larkwell_Alpha","Itemable":{"RowName":"Item_Larkwell"},"Armour":{"RowName":"Undersuit_Larkwell_Alpha"}}
            ]}
            """);
        Write("Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{"Weight":0},"Rows":[{"Name":"Item_Larkwell","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Larkwell-DisplayName\", \"Larkwell \\\"Haulier\\\" Envirosuit\")","Weight":100}]}""");
        Write("Traits/D_Armour.json", $$"""{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Undersuit_Larkwell_Alpha","ArmourStats":{{Stats}},"ArmourMesh":"/Game/ASS/Suit.Suit"}]}""");
        Write("Stats/D_Stats.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"BaseColdResistance_%"},{"Name":"BaseMovementSpeed_+%"}]}""");
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

    /// <summary>The mod: 72 backpack slots and cold resistance added, the game's upgrade slots left out; and a renamed suit.</summary>
    private ExmodEditorViewModel OpenMod()
    {
        var entry = _library.CreateBlankMod("Cheat", "Me", ModTemplate.Blank);
        var package = ExmodFolder.Read(_library.GetFolderPath(entry.FolderName)).Package;
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Armour.json",
            FileItems = [new ExmodFileItem { Name = "Undersuit_Larkwell_Alpha", Fields =
            {
                ["ArmourStats"] = JsonNode.Parse("""{"(Value=\"BaseBackpackSlots_+\")":72,"(Value=\"BaseOxygenSlots_+\")":1,"(Value=\"BaseColdResistance_%\")":15}"""),
                ["ArmourMesh"] = JsonValue.Create("/Game/ASS/OtherSuit.OtherSuit"),
            } }],
        });
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            FileItems = [new ExmodFileItem { Name = "Item_Larkwell", Fields =
            {
                ["DisplayName"] = JsonValue.Create("NSLOCTEXT(\"D_Itemable\", \"Item_Larkwell-DisplayName\", \"Haulier Mk II\")"),
                ["Weight"] = JsonValue.Create(50),
            } }],
        });
        ExmodFolder.Write(_library.GetFolderPath(entry.FolderName), new ExmodPackageContents(package, []));
        return new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache());
    }

    private static EditorFieldViewModel Field(ExmodEditorViewModel editor, string name) => editor.Fields.Single(f => f.FieldName == name);

    [Fact]
    public void TheList_UsesInGameNames_GroupsByPart_AndCountsChangedValues()
    {
        var editor = OpenMod();

        var armour = editor.Items.Single(i => i.ItemName == "Undersuit_Larkwell_Alpha");
        Assert.Equal("Larkwell \"Haulier\" Envirosuit", armour.Title);
        Assert.Equal("Armour", armour.Group);
        Assert.Equal("Envirosuit_Larkwell_Alpha", armour.GameItem);
        Assert.Equal(4, armour.ChangeCount); // backpack changed, cold added, upgrade left out, mesh swapped
        Assert.Equal("Names, weights, stacks", editor.Items.Single(i => i.ItemName == "Item_Larkwell").Group);
    }

    [Fact]
    public void Stats_AreOneRowEach_WithWhatTheModAddsAndLeavesOut()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Undersuit_Larkwell_Alpha");

        var stats = Field(editor, "ArmourStats");
        Assert.Equal(EditorFieldKind.Stats, stats.Kind);
        Assert.Equal(["Backpack slots", "Oxygen slots", "Cold resistance %", "Upgrade slots"], stats.Stats.Select(s => s.Label));
        Assert.Equal("game: 6", stats.Stats[0].GameText);
        Assert.True(stats.Stats[2].IsAdded);
        Assert.True(stats.Stats[3].IsDropped); // the mod's list replaces the game's: upgrade slots are gone in game
        Assert.Contains("gone in game", stats.Stats[3].GameText);
        Assert.Equal(EditorFieldKind.String, Field(editor, "ArmourMesh").Kind);
    }

    [Fact]
    public void EditingAStatRow_RewritesTheField_AndPutBackRestoresAGameStat()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Undersuit_Larkwell_Alpha");
        var stats = Field(editor, "ArmourStats");

        stats.Stats[0].ValueText = "100";
        stats.Stats[3].PutBackCommand.Execute(null);
        stats.Stats[2].RemoveCommand.Execute(null); // an added stat goes entirely

        var written = (JsonObject)JsonNode.Parse(stats.ValueText)!;
        Assert.Equal(100, written["(Value=\"BaseBackpackSlots_+\")"]!.GetValue<long>());
        Assert.Equal(4, written["(Value=\"BaseUpgradeSlots_+\")"]!.GetValue<long>());
        Assert.False(written.ContainsKey("(Value=\"BaseColdResistance_%\")"));
        Assert.DoesNotContain(stats.Stats, s => s.Label == "Cold resistance %");
    }

    [Fact]
    public void AddingAStat_UsesThePicker()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Undersuit_Larkwell_Alpha");
        var stats = Field(editor, "ArmourStats");

        stats.Picker!.StatSearch = "movement";
        stats.Picker.SelectedStat = Assert.Single(stats.Picker.MatchingStats);
        stats.Picker.AddStatCommand.Execute(null);

        Assert.Contains("BaseMovementSpeed_+%", stats.ValueText);
        Assert.Equal("Movement speed +%", stats.Stats[^1].Label);
    }

    [Fact]
    public void GameText_IsEditedAsPlainText_KeepingItsKey()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        var name = Field(editor, "DisplayName");
        Assert.Equal(EditorFieldKind.Text, name.Kind);
        Assert.Equal("Haulier Mk II", name.PlainText);
        Assert.Equal("game: Larkwell \"Haulier\" Envirosuit", name.BasePlainText);

        name.PlainText = "Haulier \"Mk III\"";

        Assert.Equal("NSLOCTEXT(\"D_Itemable\", \"Item_Larkwell-DisplayName\", \"Haulier \\\"Mk III\\\"\")", JsonNode.Parse(name.ValueText)!.GetValue<string>());
    }

    [Fact]
    public void ANumberField_SaysPlainlyWhenItIsntANumber_AndKeepsTheLastGoodValue()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        var weight = Field(editor, "Weight");
        Assert.Equal("Weight", weight.Label);
        Assert.Contains("Lower = lighter", weight.Hint);

        weight.ValueText = "heavy";

        Assert.Equal("Needs a number, like 50 or 0.5.", weight.ParseError);
        editor.SaveCommand.Execute(null);
        var saved = ExmodFolder.Read(_library.GetFolderPath(_library.GetAll().Single().FolderName)).Package;
        Assert.Equal(50, saved.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single().Fields["Weight"]!.GetValue<int>());
    }

    [Fact]
    public void OpenInEditor_ShowsTheGameItemOnTheEditorPage()
    {
        var editor = OpenMod();
        var page = new GameEditorViewModel(_library, DataFolder);
        string? navigated = null;
        WeakReferenceMessenger.Default.Register<NavigateToPageMessage>(this, (_, m) => navigated = m.NavItemId);
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Undersuit_Larkwell_Alpha");

        try
        {
            editor.OpenInGameEditorCommand.Execute(null);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<NavigateToPageMessage>(this);
        }

        Assert.Equal("editor", navigated);
        Assert.Equal("Envirosuit_Larkwell_Alpha", page.SelectedItem?.Name);
        Assert.Equal(_library.GetAll().Single().FolderName, page.SelectedTarget?.FolderName); // this mod's values, saved back into it
        GC.KeepAlive(page);
    }

    /// <summary>A review finding: the game value came from a cache of differing fields, so after an edit and reselect "game:" showed the mod's own value.</summary>
    [Fact]
    public void TheGameValue_StaysTheGames_AfterEditingAndReselecting()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        Field(editor, "Weight").ValueText = "75";
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Undersuit_Larkwell_Alpha");
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");

        Assert.Equal("game: 100", Field(editor, "Weight").BasePlainText);
        Assert.True(Field(editor, "Weight").IsChanged);
    }

    /// <summary>A review finding: a stat box being typed into (not a number yet) was saved as 0 when another stat changed.</summary>
    [Fact]
    public void AStatBeingTyped_KeepsItsLastGoodValue_WhenAnotherStatChanges()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Undersuit_Larkwell_Alpha");
        var stats = Field(editor, "ArmourStats");

        stats.Stats.Single(s => s.Label == "Cold resistance %").ValueText = "";
        stats.Stats.Single(s => s.Label == "Backpack slots").ValueText = "80";

        Assert.Equal(15, JsonNode.Parse(stats.ValueText)!["(Value=\"BaseColdResistance_%\")"]!.GetValue<long>());
    }

    /// <summary>A review finding: emptying a number box wrote null, taking the field out of the mod without a word.</summary>
    [Fact]
    public void EmptyingANumberBox_IsAnError_NotARemoval()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");

        Field(editor, "Weight").ValueText = "";

        Assert.Equal("Needs a number, like 50 or 0.5.", Field(editor, "Weight").ParseError);
    }

    [Fact]
    public void TheChangeBadge_FollowsEdits()
    {
        var editor = OpenMod();
        var row = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        Assert.Equal(2, row.ChangeCount); // name and weight
        editor.SelectedItem = row;

        Field(editor, "Weight").ValueText = "100"; // back to the game's

        Assert.Equal(1, row.ChangeCount);
    }

    /// <summary>A review finding: closing the editor threw typed changes away without a word.</summary>
    [Fact]
    public void Closing_WithUnsavedChanges_AsksSaveDontSaveOrCancel()
    {
        var editor = OpenMod();
        Assert.False(editor.HasUnsavedChanges);
        Assert.True(editor.ConfirmClose());
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        Field(editor, "Weight").ValueText = "75";
        Assert.True(editor.HasUnsavedChanges);
        string? asked = null;

        editor.AskSaveOnClose = message => { asked = message; return null; };
        Assert.False(editor.ConfirmClose()); // Cancel: stays open
        Assert.Contains("Cheat", asked);
        editor.AskSaveOnClose = _ => false;
        Assert.True(editor.ConfirmClose()); // Don't save
        editor.AskSaveOnClose = _ => true;
        Assert.True(editor.ConfirmClose()); // Save

        Assert.False(editor.HasUnsavedChanges);
        var saved = ExmodFolder.Read(_library.GetFolderPath(_library.GetAll().Single().FolderName)).Package;
        Assert.Equal(75, saved.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single().Fields["Weight"]!.GetValue<int>());
    }

    [Fact]
    public void Save_WhenTheModsFolderIsGone_SaysSo_InsteadOfMakingAnOrphanFolder()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        Field(editor, "Weight").ValueText = "75";
        var folder = _library.GetFolderPath(_library.GetAll().Single().FolderName);
        Directory.Delete(folder, recursive: true);

        editor.SaveCommand.Execute(null);

        Assert.StartsWith("Not saved: this mod's folder is gone", editor.StatusMessage);
        Assert.False(Directory.Exists(folder));
        editor.AskSaveOnClose = _ => true;
        Assert.False(editor.ConfirmClose()); // the save didn't happen: stays open with the reason showing
    }

    /// <summary>A review finding: Undo restored the snapshot from before the last structural action, wiping values typed since.</summary>
    [Fact]
    public void Undo_TakesBackTypingFirst_ThenTheActionBeforeIt()
    {
        var editor = OpenMod();
        editor.UndoCommand.Execute(null);
        Assert.Equal("Nothing to undo.", editor.StatusMessage);
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        editor.NewFieldName = "MaxStack";
        editor.NewFieldValue = "5";
        editor.AddFieldCommand.Execute(null);
        Field(editor, "Weight").ValueText = "7";
        Field(editor, "Weight").ValueText = "75";

        editor.UndoCommand.Execute(null);

        Assert.Equal("50", Field(editor, "Weight").ValueText);
        Assert.Contains(editor.Fields, f => f.FieldName == "MaxStack");

        editor.UndoCommand.Execute(null);

        Assert.DoesNotContain(editor.Fields, f => f.FieldName == "MaxStack");
        Assert.False(editor.CanUndo);
    }

    /// <summary>Undo had no Redo: what Undo takes away comes back with Redo (Ctrl+Y), step by step, until a new change is made.</summary>
    [Fact]
    public void Redo_PutsBackWhatUndoTook_AndANewChangeEndsTheChain()
    {
        var editor = OpenMod();
        Assert.False(editor.CanRedo);
        editor.RedoCommand.Execute(null);
        Assert.Equal("Nothing to redo.", editor.StatusMessage);
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        editor.NewFieldName = "MaxStack";
        editor.NewFieldValue = "5";
        editor.AddFieldCommand.Execute(null);
        Field(editor, "Weight").ValueText = "75";

        editor.UndoCommand.Execute(null);
        editor.UndoCommand.Execute(null);
        Assert.DoesNotContain(editor.Fields, f => f.FieldName == "MaxStack");
        Assert.True(editor.CanRedo);

        editor.RedoCommand.Execute(null);
        Assert.Contains(editor.Fields, f => f.FieldName == "MaxStack");
        Assert.Equal("50", Field(editor, "Weight").ValueText);

        editor.RedoCommand.Execute(null);
        Assert.Equal("75", Field(editor, "Weight").ValueText);
        Assert.False(editor.CanRedo);
        Assert.True(editor.CanUndo);

        editor.UndoCommand.Execute(null);
        Assert.True(editor.CanRedo);
        Field(editor, "Weight").ValueText = "60";
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void RemoveField_UpdatesTheChangeBadge()
    {
        var editor = OpenMod();
        var row = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        editor.SelectedItem = row;

        editor.RemoveFieldCommand.Execute(Field(editor, "Weight"));

        Assert.Equal(1, row.ChangeCount);
    }

    /// <summary>A review finding: typing in the filter box lost the selection and emptied the Fields pane.</summary>
    [Fact]
    public void Filtering_KeepsTheSelectedItem_WhileItStillShows()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");

        editor.FilterText = "Item_Lark";
        editor.ApplyFilter();

        Assert.Equal("Item_Larkwell", editor.SelectedItem?.ItemName);
        Assert.Contains(editor.SelectedItem!, editor.Items);
        Assert.Contains(editor.Fields, f => f.FieldName == "Weight");

        editor.FilterText = "Undersuit_Larkwell";
        editor.ApplyFilter();

        Assert.Null(editor.SelectedItem);
    }

    /// <summary>A review finding: the Editor page saving into a mod open here was silently overwritten by this window's next Save.</summary>
    [Fact]
    public void Save_WhenTheModChangedElsewhere_WarnsFirst_ThenOverwritesOnASecondSave()
    {
        var editor = OpenMod();
        var folder = _library.GetAll().Single().FolderName;
        var exmod = Directory.GetFiles(_library.GetFolderPath(folder), "*.EXMOD", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(exmod, DateTime.UtcNow.AddMinutes(5)); // saved by someone else

        editor.SaveCommand.Execute(null);
        Assert.StartsWith("Not saved", editor.StatusMessage);

        editor.SaveCommand.Execute(null);
        Assert.StartsWith("Saved", editor.StatusMessage);
    }

    /// <summary>Names typed with a space around them don't become a field (or row) with a space in its name.</summary>
    [Fact]
    public void AddField_TrimsTheName()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Larkwell");
        editor.NewFieldName = " MaxStack ";
        editor.NewFieldValue = "10";

        editor.AddFieldCommand.Execute(null);

        Assert.Contains(editor.Fields, f => f.FieldName == "MaxStack");
    }
}
