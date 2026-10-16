using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The editor's Scale rules view and the Quality of life template, on a real (temporary) library and game data folder.</summary>
[Collection(MessengerCollection.Name)]
public sealed class ExmodEditorScaleRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "EditorScale_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;

    private string DataFolder => Path.Combine(_root, "Data");

    public ExmodEditorScaleRulesTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":100,"MaxStack":1},"Rows":[
              {"Name":"Item_Fiber","Weight":10,"MaxStack":200},
              {"Name":"Item_Axe"}
            ]}
            """);
        Write("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","RequiredMillijoules":2500}]}""");
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

    private ExmodPackage ReadSaved(string folderName) => ExmodFolder.Read(_library.GetFolderPath(folderName)).Package;

    /// <summary>A Quality of life mod opens on its rules with the rows already built from this PC's game data; Save keeps both.</summary>
    [Fact]
    public void QualityOfLifeTemplate_OpensWithRowsBuiltFromGameData()
    {
        var entry = _library.CreateBlankMod("My QoL", "Me", ModTemplate.QualityOfLife);
        var editor = Open(entry.FolderName);

        Assert.Equal(ExmodEditorViewMode.ScaleRules, editor.ViewMode);
        Assert.Equal(ModTemplateContent.QualityOfLifeRules().Count, editor.ScaleRuleRows.Count);
        Assert.Contains("Applied", editor.ScaleStatusMessage);
        Assert.Contains(editor.Items, i => i.ItemName == "Item_Fiber");
        Assert.False(editor.CanUndo);

        editor.SaveCommand.Execute(null);

        var saved = ReadSaved(entry.FolderName);
        Assert.Equal(ModTemplateContent.QualityOfLifeRules().Count, saved.ScaleRules.Count);
        var items = saved.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems;
        Assert.Equal(800, items.Single(i => i.Name == "Item_Fiber").Fields["MaxStack"]!.GetValue<long>());
        // Stacks skip items that only inherit a stack of 1 (tools), but weight still halves them.
        var axe = items.Single(i => i.Name == "Item_Axe");
        Assert.False(axe.Fields.ContainsKey("MaxStack"));
        Assert.Equal(50, axe.Fields["Weight"]!.GetValue<long>());
    }

    /// <summary>Editing a rule and saving applies it first, so the saved rows always match the saved rules.</summary>
    [Fact]
    public void EditedRule_IsAppliedOnSave_AndUndoTakesItBack()
    {
        var entry = _library.CreateBlankMod("Light", "Me", ModTemplate.Blank);
        var editor = Open(entry.FolderName);
        editor.AddScaleRuleCommand.Execute(null);
        var rule = editor.ScaleRuleRows.Single();
        rule.CurrentFile = "Traits-D_Itemable.json";
        rule.Field = "Weight";
        rule.ValueText = "0";

        editor.SaveCommand.Execute(null);

        var saved = ReadSaved(entry.FolderName);
        Assert.Equal(0, saved.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single(i => i.Name == "Item_Fiber").Fields["Weight"]!.GetValue<long>());
        Assert.Single(saved.ScaleRules);

        editor.UndoCommand.Execute(null);
        Assert.Empty(editor.ScaleRuleRows);
        Assert.DoesNotContain(editor.Items, i => i.ItemName == "Item_Fiber");
    }

    [Fact]
    public void InvalidRule_BlocksSaveWithTheReason()
    {
        var entry = _library.CreateBlankMod("Broken", "Me", ModTemplate.Blank);
        var editor = Open(entry.FolderName);
        editor.AddScaleRuleCommand.Execute(null);
        editor.ScaleRuleRows.Single().ValueText = "half";

        editor.SaveCommand.Execute(null);

        Assert.Contains("must be a number", editor.StatusMessage);
        Assert.Empty(ReadSaved(entry.FolderName).ScaleRules);
    }

    [Fact]
    public void Preview_SaysWhatARuleWouldChange_WithoutChangingTheMod()
    {
        var entry = _library.CreateBlankMod("Fast", "Me", ModTemplate.Blank);
        var editor = Open(entry.FolderName);
        editor.AddScaleRuleCommand.Execute(null);
        var rule = editor.ScaleRuleRows.Single();
        rule.CurrentFile = "Crafting-D_ProcessorRecipes.json";
        rule.Field = "RequiredMillijoules";
        rule.ValueText = "0.5";

        rule.RefreshPreview();

        Assert.Equal("Changes 1 row(s), 1 value(s) — e.g. Stone_Pickaxe: 2500 → 1250", rule.PreviewText);
        Assert.Empty(editor.Items);
        Assert.Contains("RequiredMillijoules", rule.FieldOptions);
    }
}
