using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Fields a Scale rule writes: the "set by rule" tag, the notice when one is changed by hand
/// (Leave it to the rule / Keep my value), and the question before Apply or Save would replace a
/// hand-set value. On a real (temporary) library and game data folder.
/// </summary>
[Collection(MessengerCollection.Name)]
public sealed class ExmodEditorRuleOwnedFieldsTests : IDisposable
{
    private const string Itemable = "Traits-D_Itemable.json";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "EditorRuleOwned_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public ExmodEditorRuleOwnedFieldsTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        var path = Path.Combine(DataFolder, "Traits", "D_Itemable.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"RowStruct":"S","Defaults":{"Weight":100,"MaxStack":1},"Rows":[
              {"Name":"Item_Fiber","Weight":10,"MaxStack":200},
              {"Name":"Item_Stone","Weight":20,"MaxStack":50},
              {"Name":"Item_Axe","Weight":800}
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

    /// <summary>
    /// A mod with the rule "Lighter (Weight ×0.5)" on Fiber and Stone, applied; Fiber also has a hand
    /// MaxStack (no rule), and the mod adds its own Item_New.
    /// </summary>
    private string MakeMod()
    {
        var entry = _library.CreateBlankMod("Rules", "Me", ModTemplate.Blank);
        var folder = _library.GetFolderPath(entry.FolderName);
        var package = ExmodFolder.Read(folder).Package;
        var rule = new ScaleRule { Label = "Lighter", CurrentFile = Itemable, Field = "Weight", Value = 0.5, RowFilter = "Fiber, Stone" };
        ScaleRules.ApplyToPackage(package, [rule], DataFolder, new MergeReport());
        var items = package.Rows.Single(r => r.CurrentFile == Itemable).FileItems;
        items.Single(i => i.Name == "Item_Fiber").Fields["MaxStack"] = 999;
        items.Add(new ExmodFileItem { Name = "Item_New", Fields = new() { ["Weight"] = 3 } });
        ExmodFolder.Write(folder, new ExmodPackageContents(package, []));
        return entry.FolderName;
    }

    private ExmodEditorViewModel Open(string folderName) => new(folderName, _library, DataFolder, new GameDataIndexCache())
    {
        // Any question not expected by a test fails it, instead of opening a window.
        AskKeepHandEdits = message => throw new Xunit.Sdk.XunitException($"Unexpected question: {message}"),
    };

    private static EditorFieldViewModel Field(ExmodEditorViewModel editor, string row, string field)
    {
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == row);
        return editor.Fields.Single(f => f.FieldName == field);
    }

    private ExmodFileItem Saved(string folderName, string row) =>
        ExmodFolder.Read(_library.GetFolderPath(folderName)).Package.Rows.Single(r => r.CurrentFile == Itemable).FileItems.Single(i => i.Name == row);

    [Fact]
    public void RuleTag_ShowsOnTheFieldsARuleWrites_Only()
    {
        var editor = Open(MakeMod());

        var weight = Field(editor, "Item_Fiber", "Weight");
        Assert.True(weight.IsSetByRule);
        Assert.Equal("set by rule: Lighter (Weight ×0.5)", weight.RuleTag);
        Assert.False(weight.RuleWillOverwrite);
        Assert.False(Field(editor, "Item_Fiber", "MaxStack").IsSetByRule); // no rule for MaxStack
        Assert.False(Field(editor, "Item_New", "Weight").IsSetByRule);     // a row the mod adds: rules never touch it
    }

    [Fact]
    public void RuleTag_FollowsARuleChangedInTheScaleView()
    {
        var editor = Open(MakeMod());
        Assert.True(Field(editor, "Item_Fiber", "Weight").IsSetByRule);

        editor.ViewMode = ExmodEditorViewMode.ScaleRules;
        editor.ScaleRuleRows.Single().RowFilter = "Stone";
        editor.ViewMode = ExmodEditorViewMode.ItemFields;

        Assert.False(editor.Fields.Single(f => f.FieldName == "Weight").IsSetByRule);
    }

    /// <summary>Typing over a rule's value says the rule will put it back; ↺ Leave it to the rule restores the rule's value.</summary>
    [Fact]
    public void HandEdit_SaysTheRuleWillSetItAgain_AndLeaveItToTheRuleGoesBack()
    {
        var editor = Open(MakeMod());
        var weight = Field(editor, "Item_Fiber", "Weight");

        weight.ValueText = "3";

        Assert.True(weight.RuleWillOverwrite);
        Assert.Contains("puts back 5", weight.RuleOverwriteText);

        weight.LeaveToRuleCommand.Execute(null);

        Assert.Equal("5", weight.ValueText);
        Assert.False(weight.RuleWillOverwrite);
    }

    /// <summary>Apply asks once, listing the hand-set values: Cancel changes nothing, Use the rule replaces them.</summary>
    [Fact]
    public void HandEdit_ThenApply_Asks_CancelKeepsAll_UseTheRuleReplaces()
    {
        var editor = Open(MakeMod());
        Field(editor, "Item_Fiber", "Weight").ValueText = "3";
        string? asked = null;
        editor.AskKeepHandEdits = message => { asked = message; return null; };

        editor.ApplyScaleRulesCommand.Execute(null);

        Assert.Contains("Item_Fiber › Weight: yours 3, the rule's 5 (Lighter (Weight ×0.5))", asked);
        Assert.DoesNotContain("Item_Stone", asked);
        Assert.Contains("cancelled", editor.ScaleStatusMessage);
        Assert.Equal("3", Field(editor, "Item_Fiber", "Weight").ValueText);

        editor.AskKeepHandEdits = _ => false;
        editor.ApplyScaleRulesCommand.Execute(null);

        Assert.Equal("5", Field(editor, "Item_Fiber", "Weight").ValueText);
        Assert.Empty(editor.ScaleRuleRows.Single().ExceptRows);
    }

    /// <summary>Keep my value puts the row on the rule's except list: it survives Apply, Save, and opening the mod again from disk.</summary>
    [Fact]
    public void KeepMyValue_SurvivesApplySaveAndReload()
    {
        var folder = MakeMod();
        var editor = Open(folder);
        var weight = Field(editor, "Item_Fiber", "Weight");
        weight.ValueText = "3";

        weight.KeepMyValueCommand.Execute(null);

        Assert.Equal(["Item_Fiber"], editor.ScaleRuleRows.Single().ExceptRows);
        Assert.False(weight.IsSetByRule);
        Assert.Contains("skips this row", weight.RuleExceptionText);

        editor.ApplyScaleRulesCommand.Execute(null); // no question: the row is excepted
        Assert.Equal("3", Field(editor, "Item_Fiber", "Weight").ValueText);
        Assert.Equal("10", Field(editor, "Item_Stone", "Weight").ValueText); // 20 × 0.5, still the rule's

        editor.SaveCommand.Execute(null);
        Assert.Equal(3, Saved(folder, "Item_Fiber").Fields["Weight"]!.GetValue<int>());

        var reopened = Open(folder);
        Assert.Equal(["Item_Fiber"], reopened.ScaleRuleRows.Single().ExceptRows);
        reopened.ScaleRuleRows.Single().ValueText = "0.25"; // a rule change: Save applies again
        reopened.SaveCommand.Execute(null);

        Assert.Equal(3, Saved(folder, "Item_Fiber").Fields["Weight"]!.GetValue<int>());
        Assert.Equal(5, Saved(folder, "Item_Stone").Fields["Weight"]!.GetValue<long>());
        Assert.Equal(["Item_Fiber"], ExmodFolder.Read(_library.GetFolderPath(folder)).Package.ScaleRules.Single().ExceptRows);
    }

    /// <summary>Save after a rule change asks too; Keep mine there keeps the value and lists the row on the rule's card.</summary>
    [Fact]
    public void HandEdit_ThenSaveAfterARuleChange_Asks_AndKeepMineKeepsIt()
    {
        var folder = MakeMod();
        var editor = Open(folder);
        Field(editor, "Item_Fiber", "Weight").ValueText = "7";
        editor.ScaleRuleRows.Single().ValueText = "0.25";
        var asks = 0;
        editor.AskKeepHandEdits = _ => { asks++; return true; };

        editor.SaveCommand.Execute(null);

        Assert.Equal(1, asks);
        Assert.Equal(7, Saved(folder, "Item_Fiber").Fields["Weight"]!.GetValue<int>());
        Assert.Equal(5, Saved(folder, "Item_Stone").Fields["Weight"]!.GetValue<long>());
        Assert.Equal(["Item_Fiber"], editor.ScaleRuleRows.Single().ExceptRows);

        // ✕ on the card: the next Apply sets the row from the rule again (and asks first).
        editor.ScaleRuleRows.Single().RemoveExceptionCommand.Execute("Item_Fiber");
        editor.ScaleRuleRows.Single().ValueText = "0.5";
        asks = 0;
        editor.AskKeepHandEdits = _ => { asks++; return false; };
        editor.ApplyScaleRulesCommand.Execute(null);
        Assert.Equal(1, asks);
        Assert.Equal("5", Field(editor, "Item_Fiber", "Weight").ValueText);
        Assert.Empty(editor.ScaleRuleRows.Single().ExceptRows);
    }

    [Fact]
    public void CancelAtTheQuestion_StopsSave_AndSaysWhy()
    {
        var folder = MakeMod();
        var editor = Open(folder);
        Field(editor, "Item_Fiber", "Weight").ValueText = "7";
        editor.ScaleRuleRows.Single().ValueText = "0.25";
        editor.AskKeepHandEdits = _ => null;

        editor.SaveCommand.Execute(null);

        Assert.Contains("you cancelled", editor.StatusMessage);
        Assert.Equal(5, Saved(folder, "Item_Fiber").Fields["Weight"]!.GetValue<long>());
    }
}
