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

/// <summary>The fix window over a real Library folder: a prebuilt pak made for an older game (converted, like laanp's), with an old tag list, a mixed one and a real change.</summary>
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
        _library.MarkConvertedFromPrebuiltPak(_folder);
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ModFixService Service => new(_library, DataFolder, decisionsDirectory: Path.Combine(_root, "Library_Meta", "Fix_Choices"));

    private ModFixViewModel Open(Func<bool>? editorOpen = null)
    {
        var service = Service;
        return new ModFixViewModel(_folder, "Old Mod", service.Check(_folder), service, editorOpen);
    }

    private ExmodFileItem Item(string name) =>
        ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_folder)).Rows.Single(r => r.CurrentFile == "Items-D_ItemsStatic.json").FileItems.Single(i => i.Name == name);

    private LibraryEntry Entry => _library.GetAll().Single(e => e.FolderName == _folder);

    [Fact]
    public void FixSure_MarksTheModFixed_ButKeepsAConvertedPakUnedited()
    {
        Assert.Null(Entry.FixedByIslAtUtc);

        var (applied, _) = Service.FixSure(_folder);

        Assert.True(applied > 0);
        Assert.NotNull(Entry.FixedByIslAtUtc);
        Assert.False(Entry.IsLocallyEdited);
    }

    /// <summary>Sure fixes aren't remembered as choices: a re-conversion after a game update used to drop them (a broken canteen link came back).</summary>
    [Fact]
    public void ReapplyRemembered_AfterReconversion_MakesSureFixesAgain_WithNoChoicesRemembered()
    {
        var exmod = Directory.GetFiles(_library.GetFolderPath(_folder), "*.EXMOD", SearchOption.AllDirectories).Single();
        var original = File.ReadAllText(exmod);
        var service = Service;
        Assert.Equal(0, service.ReapplyRemembered(_folder));

        service.FixSure(_folder);
        Assert.Equal(0, service.RememberedCount(_folder));
        File.WriteAllText(exmod, original);

        Assert.True(service.ReapplyRemembered(_folder) > 0);
        Assert.Equal(0, service.Check(_folder).SureCount);
    }

    /// <summary>A mod fixed before the fixed mark existed: the fresh conversion has sure problems the replaced copy didn't.</summary>
    [Fact]
    public void ReapplyRemembered_MoreSureProblemsThanTheReplacedCopy_FixesAgain_AndMarksIt()
    {
        var sure = Service.Check(_folder).SureCount;
        Assert.True(sure > 0);
        Assert.Equal(0, Service.ReapplyRemembered(_folder, sureBefore: sure));

        Assert.True(Service.ReapplyRemembered(_folder, sureBefore: 0) > 0);
        Assert.NotNull(Entry.FixedByIslAtUtc);
    }

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
    public void Apply_BacksUpAndFixes_AndAConvertedPakStaysLive()
    {
        var vm = Open();
        var closed = false;
        vm.Close = () => closed = true;

        vm.ApplyCommand.Execute(null);

        Assert.True(closed);
        Assert.True(_library.HasModBackup(_folder));
        Assert.False(_library.GetAll().Single(e => e.FolderName == _folder).IsLocallyEdited);
        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.Equal(5, Item("Wood_Floor").Fields["Weight"]!.GetValue<int>());
        Assert.Equal(["Food.Cooked", "Food", "Food.Dairy"], ((JsonArray)Item("Cheese").Fields["Manual_Tags"]!["GameplayTags"]!).Select(t => t!["TagName"]!.ToString()));
        Assert.False(Service.Check(_folder).HasIssues);
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
        var (applied, toReview) = Service.FixSure(_folder);

        Assert.Equal(1, applied);
        Assert.Equal(1, toReview);
        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
    }

    [Fact]
    public void YourChoices_AreRemembered_KeptOnesStopBeingAsked()
    {
        var vm = Open();
        vm.Close = () => { };
        vm.Groups.Single(g => g.Kind == ModIssueKind.MixedList).Issues[0].Choose(ModIssueFix.KeepMod);

        vm.ApplyCommand.Execute(null);

        var again = Open();
        Assert.False(again.HasIssues);
        Assert.Equal(1, again.RememberedCount);
        Assert.Equal(2, again.AuthorChangeCount);

        again.ForgetChoicesCommand.Execute(null);
        Assert.Equal(0, again.RememberedCount);
        Assert.Equal(ModIssueKind.MixedList, Assert.Single(again.Groups).Kind);
    }

    [Fact]
    public void KeepingEverything_IsRememberedWithoutTouchingTheMod()
    {
        var vm = Open();
        vm.Close = () => { };
        foreach (var issue in vm.Groups.SelectMany(g => g.Issues))
        {
            issue.Choose(ModIssueFix.KeepMod);
        }

        Assert.Equal("Keep all as they are", vm.ApplyLabel);
        vm.ApplyCommand.Execute(null);

        Assert.False(_library.HasModBackup(_folder));
        Assert.False(Open().HasIssues);
    }

    /// <summary>A hand-made mod lists only what its author changed: its shorter tag list is the author's, and a fix marks it edited (✎).</summary>
    [Fact]
    public void AHandMadeMod_KeepsItsShorterList_AndAFixMarksItEdited()
    {
        var entry = _library.CreateBlankMod("My Mod", "Me", ModTemplate.Blank);
        var folderPath = _library.GetFolderPath(entry.FolderName);
        var package = ExmodFolder.Read(folderPath).Package;
        package.Rows.Insert(0, new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemsStatic.json",
            FileItems =
            [
                new ExmodFileItem { Name = "Wood_Floor", Fields = new() { ["Manual_Tags"] = Tags("Building.Wood") } },
                new ExmodFileItem { Name = "Cheese", Fields = new() { ["Manual_Tags"] = Tags("Food", "Food.Dairy") } },
            ],
        });
        ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
        var service = Service;
        var vm = new ModFixViewModel(entry.FolderName, "My Mod", service.Check(entry.FolderName), service) { Close = () => { } };

        Assert.Equal(["Lists both changed"], vm.Groups.Select(g => g.Title));
        Assert.Equal(1, vm.AuthorChangeCount);
        vm.ApplyCommand.Execute(null);

        Assert.True(_library.GetAll().Single(e => e.FolderName == entry.FolderName).IsLocallyEdited);
    }

    /// <summary>Values that already match the game change nothing: shown as optional tidying, never as a problem (no warning badge).</summary>
    [Fact]
    public void ValuesTheGameAlreadyHas_AreTidyingNotProblems()
    {
        var entry = _library.CreateBlankMod("Tidy Mod", "Me", ModTemplate.Blank);
        var folderPath = _library.GetFolderPath(entry.FolderName);
        var package = ExmodFolder.Read(folderPath).Package;
        package.Rows.Insert(0, new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemsStatic.json",
            FileItems = [new ExmodFileItem { Name = "Cheese", Fields = new() { ["Weight"] = 50, ["Manual_Tags"] = Tags("Food.Cooked") } }],
        });
        ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
        var report = Service.Check(entry.FolderName);

        Assert.Equal(2, report.Count(ModIssueKind.SameAsGame));
        Assert.False(report.HasProblems);
        var vm = new ModFixViewModel(entry.FolderName, "Tidy Mod", report, Service);
        Assert.StartsWith("Nothing here is wrong", vm.Summary);
        Assert.Equal("the same (no change)", vm.Groups.Single().Issues[0].GameText);
    }

    /// <summary>A converted prebuilt pak stays live: fixing it doesn't mark it edited, and after the next re-conversion the choices are made again.</summary>
    [Fact]
    public void AConvertedPak_KeepsRefreshing_AndYourChoicesAreMadeAgainAfterward()
    {
        _library.MarkConvertedFromPrebuiltPak(_folder);
        var original = File.ReadAllText(Directory.GetFiles(_library.GetFolderPath(_folder), "*.EXMOD", SearchOption.AllDirectories).Single());
        var vm = Open();
        vm.Close = () => { };
        vm.ApplyCommand.Execute(null);
        Assert.False(_library.GetAll().Single(e => e.FolderName == _folder).IsLocallyEdited);

        // What the refresh after a game update does: the mod is converted again from its pak.
        File.WriteAllText(Directory.GetFiles(_library.GetFolderPath(_folder), "*.EXMOD", SearchOption.AllDirectories).Single(), original);
        Assert.True(Item("Cheese").Fields["Manual_Tags"]!["GameplayTags"]!.AsArray().Count == 2);

        Assert.Equal(2, Service.ReapplyRemembered(_folder));
        Assert.Equal(3, Item("Cheese").Fields["Manual_Tags"]!["GameplayTags"]!.AsArray().Count);
        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
    }

    /// <summary>"Keep all as they are": the choices are remembered, the mod isn't touched, and the window says nothing was applied.</summary>
    [Fact]
    public void KeepAll_AppliesNothing_ButRemembersTheChoices()
    {
        var vm = Open();
        var closed = false;
        vm.Close = () => closed = true;
        foreach (var issue in vm.Groups.SelectMany(g => g.Issues))
        {
            issue.Choose(ModIssueFix.KeepMod);
        }

        Assert.Equal("Keep all as they are", vm.ApplyLabel);
        vm.ApplyCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(0, vm.AppliedCount);
        Assert.False(_library.HasModBackup(_folder));
        Assert.Equal(2, Service.RememberedCount(_folder));
    }

    /// <summary>One Fix sure click is one backup, so Restore latest backup undoes all of it.</summary>
    [Fact]
    public void FixSure_BacksUpOnce()
    {
        var backups = Path.Combine(_root, "Backups");

        Assert.Equal(1, Service.FixSure(_folder).Applied);

        Assert.Single(Directory.GetDirectories(backups, "*", SearchOption.AllDirectories).Where(d => Path.GetFileName(d).StartsWith(_folder, StringComparison.OrdinalIgnoreCase)));
        Assert.True(Service.RestoreBackup(_folder));
        Assert.True(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
    }

    /// <summary>Calls from several threads at once (the fix window, Fix sure problems, the library check) are taken one at a time.</summary>
    [Fact]
    public async Task ConcurrentChecks_ShareOneServiceSafely()
    {
        var service = Service;
        var reports = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.Check(_folder))));

        Assert.All(reports, r => Assert.Equal(2, r.Issues.Count));
    }

    private static JsonObject Tags(params string[] tags) =>
        new() { ["GameplayTags"] = new JsonArray(tags.Select(t => (JsonNode)new JsonObject { ["TagName"] = t }).ToArray()) };
}
