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

/// <summary>The fix window over a real Library folder: a mod made for an older game, with an old tag list, a mixed one and a real change.</summary>
[Collection(MessengerCollection.Name)]
public sealed class ModFixViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ModFix_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;
    private readonly string _folder;

    private string DataFolder => Path.Combine(_root, "Data");

    public ModFixViewModelTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        var path = Path.Combine(DataFolder, "Items", "D_ItemsStatic.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Wood_Floor","Manual_Tags":{"GameplayTags":[{"TagName":"Building.Wood"},{"TagName":"FieldGuide.Floors"}]},"Weight":100},
              {"Name":"Cheese","Manual_Tags":{"GameplayTags":[{"TagName":"Food.Cooked"}]},"Weight":50}
            ]}
            """);

        var entry = _library.CreateBlankMod("Old Mod", "Me", ModTemplate.Blank);
        _folder = entry.FolderName;
        var folderPath = _library.GetFolderPath(_folder);
        var package = ExmodFolder.Read(folderPath).Package;
        package.Rows.Insert(0, new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemsStatic.json",
            FileItems =
            [
                new ExmodFileItem { Name = "Wood_Floor", Fields = new() { ["Manual_Tags"] = Tags("Building.Wood"), ["Weight"] = 5 } },
                new ExmodFileItem { Name = "Cheese", Fields = new() { ["Manual_Tags"] = Tags("Food", "Food.Dairy") } },
            ],
        });
        ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ModFixViewModel Open(Func<bool>? editorOpen = null)
    {
        var service = new ModFixService(_library, DataFolder);
        return new ModFixViewModel(_folder, "Old Mod", service.Check(_folder), service, editorOpen);
    }

    private ExmodFileItem Item(string name) =>
        ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_folder)).Rows.Single(r => r.CurrentFile == "Items-D_ItemsStatic.json").FileItems.Single(i => i.Name == name);

    [Fact]
    public void TheWindow_SortsByKind_WithTheSuggestedFixChosen()
    {
        var vm = Open();

        Assert.Equal(["Old copies of game data", "Lists both changed"], vm.Groups.Select(g => g.Title));
        Assert.Equal(1, vm.AuthorChangeCount);
        Assert.Equal(2, vm.ChangeCount);
        Assert.Contains("only here: FieldGuide.Floors", vm.Groups[0].Issues[0].GameText);
        Assert.Equal("Merge both", vm.Groups[1].Issues[0].SelectedChoice.Label);
    }

    [Fact]
    public void Apply_BacksUpFixesAndMarksTheModEdited()
    {
        var vm = Open();
        var closed = false;
        vm.Close = () => closed = true;

        vm.ApplyCommand.Execute(null);

        Assert.True(closed);
        Assert.True(_library.HasModBackup(_folder));
        Assert.True(_library.GetAll().Single(e => e.FolderName == _folder).IsLocallyEdited);
        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.Equal(5, Item("Wood_Floor").Fields["Weight"]!.GetValue<int>());
        Assert.Equal(["Food.Cooked", "Food", "Food.Dairy"], ((JsonArray)Item("Cheese").Fields["Manual_Tags"]!["GameplayTags"]!).Select(t => t!["TagName"]!.ToString()));
        Assert.False(new ModFixService(_library, DataFolder).Check(_folder).HasIssues);
    }

    [Fact]
    public void OnlyTheSureFixes_LeavesTheListsYouHaventLookedAt()
    {
        var vm = Open();
        vm.Close = () => { };

        vm.OnlySureCommand.Execute(null);
        Assert.Equal(1, vm.ChangeCount);
        vm.ApplyCommand.Execute(null);

        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.Equal(2, ((JsonArray)Item("Cheese").Fields["Manual_Tags"]!["GameplayTags"]!).Count);
    }

    [Fact]
    public void WhileTheModIsOpenInTheEditor_NothingIsWritten()
    {
        var vm = Open(editorOpen: () => true);

        vm.ApplyCommand.Execute(null);

        Assert.Contains("open in the mod editor", vm.StatusMessage);
        Assert.False(_library.HasModBackup(_folder));
        Assert.True(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
    }

    [Fact]
    public void FixSure_IsTheAutomaticPart()
    {
        var (applied, toReview) = new ModFixService(_library, DataFolder).FixSure(_folder);

        Assert.Equal(1, applied);
        Assert.Equal(1, toReview);
        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
    }

    private static JsonObject Tags(params string[] tags) =>
        new() { ["GameplayTags"] = new JsonArray(tags.Select(t => (JsonNode)new JsonObject { ["TagName"] = t }).ToArray()) };
}
