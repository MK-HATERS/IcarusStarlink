using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class ExmodEditorViewModelTests
{
    private const string Itemable = "Traits-D_Itemable.json";

    /// <summary>Writes a mod with these rows (and an EndOfMod marker last), then opens it.</summary>
    private (ExmodEditorViewModel Editor, string Folder) OpenWith(string name, Action<ExmodPackage> fill)
    {
        var entry = _library.CreateBlankMod(name, "Me", ModTemplate.Blank);
        var folder = _library.GetFolderPath(entry.FolderName);
        var package = ExmodFolder.Read(folder).Package;
        fill(package);
        if (!package.Rows.Any(r => r.CurrentFile == "EndOfMod"))
        {
            package.Rows.Add(new ExmodFileRow { CurrentFile = "EndOfMod" });
        }

        ExmodFolder.Write(folder, new ExmodPackageContents(package, []));
        return (new ExmodEditorViewModel(entry.FolderName, _library, DataFolder, new GameDataIndexCache()), entry.FolderName);
    }

    private ExmodPackage Saved(string folder) => ExmodFolder.ReadPackageOnly(_library.GetFolderPath(folder));

    /// <summary>Regression: Save read every game table again; a table that couldn't be read then kept values the game already has.</summary>
    [Fact]
    public void Save_StripsWithTheTablesReadWhenTheEditorOpened()
    {
        var (editor, folder) = OpenWith("Strip", p => p.Rows.Add(new ExmodFileRow
        {
            CurrentFile = Itemable,
            FileItems =
            [
                new ExmodFileItem { Name = "Item_Fiber", Fields = { ["Weight"] = JsonValue.Create(10) } },
                new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["Weight"] = JsonValue.Create(50) } },
            ],
        }));
        File.Delete(Path.Combine(DataFolder, "Traits", "D_Itemable.json"));
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Envirosuit");
        editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "40";

        editor.SaveCommand.Execute(null);

        var fiber = Saved(folder).Rows.Single(r => r.CurrentFile == Itemable).FileItems.Single(i => i.Name == "Item_Fiber");
        Assert.False(fiber.Fields.ContainsKey("Weight"), editor.StatusMessage);
    }

    /// <summary>Regression: Ctrl+D copied the row's texts under the original's keys, so two rows shared one text key.</summary>
    [Fact]
    public void Duplicate_GivesTheCopyItsOwnTextKeys()
    {
        var (editor, folder) = OpenWith("Dup", p => p.Rows.Add(new ExmodFileRow
        {
            CurrentFile = Itemable,
            FileItems = [new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["DisplayName"] = JsonValue.Create("NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-DisplayName\", \"Suit\")") } }],
        }));
        editor.SelectedItem = editor.Items.Single(i => i.ItemName == "Item_Envirosuit");

        editor.DuplicateItemCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        var copy = Saved(folder).Rows.Single(r => r.CurrentFile == Itemable).FileItems.Single(i => i.Name == "Item_Envirosuit_Copy");
        Assert.True(NsLocText.TryParse(copy.Fields["DisplayName"]!.GetValue<string>(), out _, out var key, out var text));
        Assert.Equal(("Item_Envirosuit_Copy-DisplayName", "Suit"), (key, text));
    }

    /// <summary>Top-level keys this version doesn't read stay in the mod through edits, Undo and Save.</summary>
    [Fact]
    public void UnknownTopLevelKeys_SurviveAnEditUndoAndSave()
    {
        var (editor, folder) = OpenWith("Extra", p =>
        {
            p.ExtraFields = new JsonObject { ["Mods"] = new JsonArray("One", "Two") };
            p.Rows.Add(new ExmodFileRow { CurrentFile = Itemable, FileItems = [new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["Weight"] = JsonValue.Create(50) } }] });
        });
        editor.SelectedItem = editor.Items.Single();
        editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "40";
        editor.UndoCommand.Execute(null);
        editor.SelectedItem = editor.Items.Single();
        editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "30";

        editor.SaveCommand.Execute(null);

        Assert.Equal(["One", "Two"], Saved(folder).ExtraFields!["Mods"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    /// <summary>A field the game's code says something about shows it as a note; other fields don't.</summary>
    [Fact]
    public void RepairNeeds_ShowTheNoteFromTheGamesCode_OtherFieldsDont()
    {
        Write("Traits/D_Durable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Durable_Wall","Max_Durability":500,"ItemsForRepair":[]}]}""");
        var (editor, _) = OpenWith("Repairs", p => p.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Durable.json",
            FileItems =
            [
                new ExmodFileItem
                {
                    Name = "Durable_Wall",
                    Fields = { ["Max_Durability"] = JsonValue.Create(900), ["ItemsForRepair"] = JsonNode.Parse("""[{"Item":{"RowName":"Stone"},"Amount":1}]""") },
                },
            ],
        }));
        editor.SelectedItem = editor.Items.Single();

        Assert.Equal(KnownGameBehaviour.HammerRepairs.Note, editor.Fields.Single(f => f.FieldName == "ItemsForRepair").GameCodeNote);
        Assert.Null(editor.Fields.Single(f => f.FieldName == "Max_Durability").GameCodeNote);
    }

    /// <summary>Copy a game row into the mod puts its new table before the EndOfMod marker.</summary>
    [Fact]
    public async Task CopyAGameRow_KeepsEndOfModLast()
    {
        var (editor, folder) = OpenWith("Copied", p => p.Rows.Add(new ExmodFileRow
        {
            CurrentFile = Itemable,
            FileItems = [new ExmodFileItem { Name = "Item_Envirosuit", Fields = { ["Weight"] = JsonValue.Create(50) } }],
        }));
        editor.PickGameItem = (index, _) => index.Single(i => i.CurrentFile == "Items-D_ItemsStatic.json" && i.ItemName == "Envirosuit");

        await editor.AddItemFromGameCommand.ExecuteAsync(null);
        editor.SaveCommand.Execute(null);

        Assert.Equal("Envirosuit", editor.SelectedItem?.ItemName);
        var rows = Saved(folder).Rows;
        Assert.Contains(rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
        Assert.Equal("EndOfMod", rows[^1].CurrentFile);
    }
}
