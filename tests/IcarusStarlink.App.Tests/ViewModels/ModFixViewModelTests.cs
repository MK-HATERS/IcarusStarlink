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

    /// <summary>
    /// Every fix Fix sure problems makes is remembered, so a re-conversion after a game update makes
    /// exactly those again (it used to drop them: a broken canteen link came back).
    /// </summary>
    [Fact]
    public void ReapplyRemembered_AfterReconversion_MakesExactlyTheFixesMadeBefore()
    {
        var exmod = Directory.GetFiles(_library.GetFolderPath(_folder), "*.EXMOD", SearchOption.AllDirectories).Single();
        var original = File.ReadAllText(exmod);
        var service = Service;
        Assert.Equal(0, service.ReapplyRemembered(_folder));

        service.FixSure(_folder);
        Assert.Equal(1, service.RememberedCount(_folder));
        File.WriteAllText(exmod, original);

        Assert.Equal(1, service.ReapplyRemembered(_folder));
        Assert.Equal(0, service.Check(_folder).SureProblemCount);
        // The mixed list nobody chose a fix for stays as the mod has it.
        Assert.Equal(2, ((JsonArray)Item("Cheese").Fields["Manual_Tags"]!["GameplayTags"]!).Count);
    }

    /// <summary>A mod never fixed is never touched by the refresh, however many sure problems its fresh conversion has.</summary>
    [Fact]
    public void ReapplyRemembered_NeverFixedBefore_ChangesNothing()
    {
        Assert.True(Service.Check(_folder).SureProblemCount > 0);

        Assert.Equal(0, Service.ReapplyRemembered(_folder));

        Assert.False(_library.HasModBackup(_folder));
        Assert.True(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.Null(Entry.FixedByIslAtUtc);
    }

    /// <summary>A mod Fix sure problems fixed before it remembered its fixes (only marked fixed): fixed once more, and remembered from then on.</summary>
    [Fact]
    public void ReapplyRemembered_MarkedFixedByAnEarlierVersion_FixesItsSureProblems_AndRemembersThem()
    {
        _library.MarkFixedByIsl(_folder);

        Assert.Equal(1, Service.ReapplyRemembered(_folder));

        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.Equal(1, Service.RememberedCount(_folder));
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
    public async Task Apply_BacksUpAndFixes_AndAConvertedPakStaysLive()
    {
        var vm = Open();
        var closed = false;
        vm.Close = () => closed = true;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.True(_library.HasModBackup(_folder));
        Assert.False(_library.GetAll().Single(e => e.FolderName == _folder).IsLocallyEdited);
        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.Equal(5, Item("Wood_Floor").Fields["Weight"]!.GetValue<int>());
        Assert.Equal(["Food.Cooked", "Food", "Food.Dairy"], ((JsonArray)Item("Cheese").Fields["Manual_Tags"]!["GameplayTags"]!).Select(t => t!["TagName"]!.ToString()));
        Assert.False(Service.Check(_folder).HasIssues);
    }

    [Fact]
    public async Task OnlyTheSureFixes_LeavesTheListsYouHaventLookedAt()
    {
        var vm = Open();
        vm.Close = () => { };

        vm.OnlySureCommand.Execute(null);
        Assert.Equal(1, vm.ChangeCount);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.Equal(2, ((JsonArray)Item("Cheese").Fields["Manual_Tags"]!["GameplayTags"]!).Count);
    }

    [Fact]
    public async Task WhileTheModIsOpenInTheEditor_NothingIsWritten()
    {
        var vm = Open(editorOpen: () => true);

        await vm.ApplyCommand.ExecuteAsync(null);

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
    public async Task YourChoices_AreRemembered_KeptOnesStopBeingAsked()
    {
        var vm = Open();
        vm.Close = () => { };
        vm.Groups.Single(g => g.Kind == ModIssueKind.MixedList).Issues[0].Choose(ModIssueFix.KeepMod);

        await vm.ApplyCommand.ExecuteAsync(null);

        var again = Open();
        Assert.False(again.HasIssues);
        // The kept list, and the sure fix made with it (made again after a re-conversion).
        Assert.Equal(2, again.RememberedCount);
        Assert.Equal(2, again.AuthorChangeCount);

        await again.ForgetChoicesCommand.ExecuteAsync(null);
        Assert.Equal(0, again.RememberedCount);
        Assert.Equal(ModIssueKind.MixedList, Assert.Single(again.Groups).Kind);
    }

    [Fact]
    public async Task KeepingEverything_IsRememberedWithoutTouchingTheMod()
    {
        var vm = Open();
        vm.Close = () => { };
        foreach (var issue in vm.Groups.SelectMany(g => g.Issues))
        {
            issue.Choose(ModIssueFix.KeepMod);
        }

        Assert.Equal("Keep all as they are", vm.ApplyLabel);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.False(_library.HasModBackup(_folder));
        Assert.False(Open().HasIssues);
    }

    /// <summary>A hand-made mod lists only what its author changed: its shorter tag list is the author's, and a fix marks it edited (✎).</summary>
    [Fact]
    public async Task AHandMadeMod_KeepsItsShorterList_AndAFixMarksItEdited()
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
        await vm.ApplyCommand.ExecuteAsync(null);

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

        // Fix sure problems never tidies a mod that fits the game: nothing written, nothing backed up.
        Assert.Equal(0, report.SureProblemCount);
        Assert.Equal(0, Service.FixSure(entry.FolderName).Applied);
        Assert.False(_library.HasModBackup(entry.FolderName));
        Assert.Equal(2, Service.Check(entry.FolderName).Count(ModIssueKind.SameAsGame));
    }

    /// <summary>A converted prebuilt pak stays live: fixing it doesn't mark it edited, and after the next re-conversion the choices are made again.</summary>
    [Fact]
    public async Task AConvertedPak_KeepsRefreshing_AndYourChoicesAreMadeAgainAfterward()
    {
        _library.MarkConvertedFromPrebuiltPak(_folder);
        var original = File.ReadAllText(Directory.GetFiles(_library.GetFolderPath(_folder), "*.EXMOD", SearchOption.AllDirectories).Single());
        var vm = Open();
        vm.Close = () => { };
        await vm.ApplyCommand.ExecuteAsync(null);
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
    public async Task KeepAll_AppliesNothing_ButRemembersTheChoices()
    {
        var vm = Open();
        var closed = false;
        vm.Close = () => closed = true;
        foreach (var issue in vm.Groups.SelectMany(g => g.Issues))
        {
            issue.Choose(ModIssueFix.KeepMod);
        }

        Assert.Equal("Keep all as they are", vm.ApplyLabel);
        await vm.ApplyCommand.ExecuteAsync(null);

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

        Assert.Single(Directory.GetDirectories(backups, "*", SearchOption.AllDirectories), d => Path.GetFileName(d).StartsWith(_folder, StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void MissingGameFile_RowSaysWhatTheFilesAreAndWhereInPlainWords()
    {
        var found = new IcarusStarlink.PakIO.Assets.MissingAssetReference(
            "ASS/DEP/Wires/SM_Cable.uasset", "/Game/ASS/DEP/M_ITM_CableHand",
            new Dictionary<string, string> { ["M_ITM_CableHand"] = "Material" },
            IcarusStarlink.PakIO.Assets.MissingReferenceKind.Moved, "/Game/ASS/ITM/M_ITM_CableHand", ["/Game/ASS/ITM/M_ITM_CableHand"]);
        var row = new ModFixIssueViewModel(Assert.Single(AssetReferenceIssues.From([found])));

        Assert.Equal("SM_Cable", row.Row);
        Assert.Equal("Mod's mesh · Deployables", row.Table);
        Assert.Equal("M_ITM_CableHand", row.Field);
        Assert.Equal("game's material", row.FieldSubtitle);
        Assert.Equal("uses it from Deployables (ASS/DEP)", row.ModText);
        Assert.Equal("has it in Items (ASS/ITM)", row.GameText);
        Assert.Contains("SM_Cable is a mesh (3D model) the mod brings, kept in Deployables (benches, machines, things you place), folder ASS/DEP/Wires", row.AssetHint);
        Assert.Contains("the game keeps it in Items (tools, food, resources), folder ASS/ITM now", row.AssetHint);
    }

    /// <summary>The Tidy Mod: Cheese set to the game's own Weight 50 and the game's own tags (both only tidying).</summary>
    private string TidyMod()
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
        return entry.FolderName;
    }

    private string ExmodPath(string folder) => Directory.GetFiles(_library.GetFolderPath(folder), "*.EXMOD", SearchOption.AllDirectories).Single();

    /// <summary>Regression: a fix wrote a second .EXMOD into a folder copied from the classic mod manager (its .EXMOD at the top), and the mod dropped out of the Library.</summary>
    [Fact]
    public void AFix_InAModCopiedFromTheClassicManager_LeavesOneExmod_AndTheModStaysInTheLibrary()
    {
        var folder = Path.Combine(_root, "Extracted_Mods", "Classic");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Classic.EXMOD"), ExmodJson.Serialize(new ExmodPackage
        {
            Name = "Classic", Author = "Someone", Version = "1", Description = "", FileName = "Classic",
            Rows =
            [
                new ExmodFileRow { CurrentFile = "Items-D_ItemsStatic.json", FileItems = [new ExmodFileItem { Name = "Cheese", Fields = new() { ["Weight"] = 50 } }] },
                new ExmodFileRow { CurrentFile = "EndOfMod" },
            ],
        }));
        _library.Refresh();

        var issue = Service.Check("Classic").Issues.Single(i => i.FieldName == "Weight");
        Assert.Equal(1, Service.Apply("Classic", [(issue, ModIssueFix.UseGame)]));

        Assert.Single(Directory.GetFiles(folder, "*.EXMOD", SearchOption.AllDirectories));
        _library.Refresh();
        Assert.Empty(_library.UnreadableFolders);
        Assert.Contains(_library.GetAll(), e => e.FolderName == "Classic");
    }

    /// <summary>Regression: the fix was written before the mod's Library file was marked, so a locked Library file said "Couldn't fix the mod" after the mod had been fixed.</summary>
    [Fact]
    public void AFix_WhenTheModsLibraryFileIsLocked_WritesNothing()
    {
        var exmod = ExmodPath(_folder);
        var before = File.ReadAllText(exmod);
        var service = Service;
        var fixes = service.Check(_folder).Issues.Select(i => (i, i.DefaultFix)).ToList();

        using (new FileStream(Path.Combine(_root, "Library_Meta", _folder + ".json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<ModNotMarkedException>(() => service.Apply(_folder, fixes));
        }

        Assert.Equal(before, File.ReadAllText(exmod));
    }

    /// <summary>
    /// Regression: the choices were remembered before the fix was written, so after a failed fix
    /// the next Fix sure problems made them by itself; and the message didn't say the mod may be
    /// partly fixed.
    /// </summary>
    [Fact]
    public async Task AFailedFix_IsNotRemembered_AndSaysRestoreBackupUndoesIt()
    {
        var vm = Open();
        vm.Close = () => { };
        var exmod = ExmodPath(_folder);
        var before = File.ReadAllText(exmod);

        using (new FileStream(exmod, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await vm.ApplyCommand.ExecuteAsync(null);
        }

        Assert.StartsWith("Couldn't fix the mod", vm.StatusMessage);
        Assert.Contains("Restore backup", vm.StatusMessage);
        Assert.True(vm.HasBackup);
        Assert.Equal(0, Service.RememberedCount(_folder));
        Assert.Equal(before, File.ReadAllText(exmod));
    }

    /// <summary>Regression: a fix stopped by a locked Library file (before anything was written) said the mod "may be partly fixed".</summary>
    [Fact]
    public async Task AFixStoppedByALockedLibraryFile_DoesNotSayTheModMayBePartlyFixed()
    {
        var vm = Open();
        vm.Close = () => { };
        var exmod = ExmodPath(_folder);
        var before = File.ReadAllText(exmod);

        using (new FileStream(Path.Combine(_root, "Library_Meta", _folder + ".json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await vm.ApplyCommand.ExecuteAsync(null);
        }

        Assert.StartsWith("Couldn't fix the mod", vm.StatusMessage);
        Assert.DoesNotContain("partly fixed", vm.StatusMessage);
        Assert.Equal(before, File.ReadAllText(exmod));
    }

    /// <summary>Regression: Restore backup put the mod back but kept the choices made with that fix, so the next pass made the fix again.</summary>
    [Fact]
    public void RestoreBackup_ForgetsTheChoicesRememberedWithThatFix()
    {
        var folder = TidyMod();
        var weight = Service.Check(folder).Issues.Single(i => i.FieldName == "Weight");
        Assert.Equal(1, Service.Apply(folder, [(weight, ModIssueFix.UseGame)]));

        Assert.True(Service.RestoreBackup(folder));

        Assert.Equal(0, Service.Check(folder).SureProblemCount);
        Assert.Equal(0, Service.FixSure(folder).Applied);
        Assert.Equal(0, Service.RememberedCount(folder));
    }

    /// <summary>A choice remembered before the fix that was undone stays remembered after the restore.</summary>
    [Fact]
    public void RestoreBackup_KeepsTheChoicesMadeBeforeThatFix()
    {
        var folder = TidyMod();
        var issues = Service.Check(folder).Issues;
        var tags = issues.Single(i => i.FieldName == "Manual_Tags");
        var weight = issues.Single(i => i.FieldName == "Weight");
        Assert.Equal(0, Service.Apply(folder, [(tags, ModIssueFix.KeepMod)]));
        Assert.Equal(1, Service.Apply(folder, [(weight, ModIssueFix.UseGame)]));
        Assert.Equal(2, Service.RememberedCount(folder));

        Assert.True(Service.RestoreBackup(folder));

        Assert.Equal(1, Service.RememberedCount(folder));
        Assert.Equal(50, ExmodFolder.ReadPackageOnly(_library.GetFolderPath(folder)).Rows[0].FileItems[0].Fields["Weight"]!.GetValue<int>());
    }

    /// <summary>Versions… › Put this version back on the copy kept before a fix, with a newer backup since: that fix's choices go with it.</summary>
    [Fact]
    public void RestoreBackup_AChosenOlderBackup_PutsBackTheChoicesFromBeforeThatFix()
    {
        var folder = TidyMod();
        var issues = Service.Check(folder).Issues;
        var tags = issues.Single(i => i.FieldName == "Manual_Tags");
        var weight = issues.Single(i => i.FieldName == "Weight");
        Assert.Equal(0, Service.Apply(folder, [(tags, ModIssueFix.KeepMod)]));
        Assert.Equal(1, Service.Apply(folder, [(weight, ModIssueFix.UseGame)]));
        var beforeFix = Service.LatestBackupPath(folder)!;
        ((ILibraryRepository)_library).BackupMod(folder, ModBackupReasons.MadeByYou);
        Assert.NotEqual(beforeFix, Service.LatestBackupPath(folder));

        Assert.True(Service.RestoreBackup(folder, beforeFix));

        Assert.Equal(1, Service.RememberedCount(folder));
        Assert.Equal(50, ExmodFolder.ReadPackageOnly(_library.GetFolderPath(folder)).Rows[0].FileItems[0].Fields["Weight"]!.GetValue<int>());
    }

    /// <summary>A fix (backed up), then a mod editor save on top of it: Wood_Floor's Weight 77.</summary>
    private void FixThenSaveInTheModEditor()
    {
        Service.FixSure(_folder);
        var package = ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_folder));
        package.Rows.Single(r => r.CurrentFile == "Items-D_ItemsStatic.json").FileItems.Single(i => i.Name == "Wood_Floor").Fields["Weight"] = 77;
        Assert.True(new OwnModWriter(_library, new WeakReferenceMessenger()).TrySave(_folder, package, null, out _));
    }

    /// <summary>Regression: Restore backup replaced the mod at once, dropping every save made since the backup without a word.</summary>
    [Fact]
    public async Task RestoreBackup_AnsweredNo_KeepsAnEditorSaveMadeAfterTheBackup()
    {
        FixThenSaveInTheModEditor();
        var vm = Open();
        string? asked = null;
        vm.ConfirmRestore = m =>
        {
            asked = m;
            return false;
        };

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Contains("lost", asked);
        Assert.Equal(77, Item("Wood_Floor").Fields["Weight"]!.GetValue<int>());
        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.False(vm.ChangedWithoutApply);
    }

    [Fact]
    public async Task RestoreBackup_AnsweredYes_PutsTheModBackAsItWasAtTheBackup_AndSaysWhen()
    {
        FixThenSaveInTheModEditor();
        var vm = Open();
        string? asked = null;
        vm.ConfirmRestore = m =>
        {
            asked = m;
            return true;
        };

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Contains("latest backup (", asked);
        Assert.Equal(5, Item("Wood_Floor").Fields["Weight"]!.GetValue<int>());
        Assert.True(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
        Assert.StartsWith("Restored the mod as it was at its latest backup (", vm.StatusMessage);
        Assert.True(vm.ChangedWithoutApply);
    }

    [Fact]
    public void BackupTime_IsReadFromTheBackupsName()
    {
        Assert.Equal(new DateTime(2026, 11, 1, 14, 22, 33, DateTimeKind.Utc), ModFixService.BackupTimeUtc(Path.Combine("Backups", "Old_Mod_20261101-142233")));
        Assert.Equal(new DateTime(2026, 11, 1, 14, 22, 33, DateTimeKind.Utc), ModFixService.BackupTimeUtc(Path.Combine("Backups", "Old_Mod_20261101-142233_2")));
        Assert.Null(ModFixService.BackupTimeUtc(Path.Combine("Backups", "Old_Mod")));
    }

    /// <summary>Regression: Apply, Restore backup and Forget ran a whole-folder copy and a Library-wide check on the UI thread.</summary>
    [Fact]
    public async Task TheWindowsDiskWork_RunsThroughTheBackgroundRunner()
    {
        var vm = Open();
        var ran = 0;
        vm.RunInBackground = work =>
        {
            ran++;
            work();
            return Task.CompletedTask;
        };

        await vm.ForgetChoicesCommand.ExecuteAsync(null);

        Assert.Equal(1, ran);
    }

    /// <summary>Regression: Forget set the window's "changed" flag inside the background work, off the window's thread.</summary>
    [Fact]
    public async Task ForgetChoices_SetsTheChangedFlagOnTheWindowsThread_AfterTheBackgroundWork()
    {
        var vm = Open();
        bool? changedDuringWork = null;
        vm.RunInBackground = work =>
        {
            work();
            changedDuringWork = vm.ChangedWithoutApply;
            return Task.CompletedTask;
        };

        await vm.ForgetChoicesCommand.ExecuteAsync(null);

        Assert.False(changedDuringWork);
        Assert.True(vm.ChangedWithoutApply);
    }

    [Fact]
    public async Task WhileAFixIsBeingWritten_TheOtherButtonsWait()
    {
        var vm = Open();
        var gate = new TaskCompletionSource();
        vm.RunInBackground = work => gate.Task.ContinueWith(_ => work(), TaskScheduler.Default);
        var closed = false;
        vm.Close = () => closed = true;

        var running = vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.IsIdle);
        Assert.False(vm.RestoreBackupCommand.CanExecute(null));
        Assert.False(vm.ForgetChoicesCommand.CanExecute(null));
        Assert.False(vm.OpenEditorCommand.CanExecute(null));
        gate.SetResult();
        await running;
        Assert.False(vm.IsBusy);
        Assert.True(closed);
        Assert.False(Item("Wood_Floor").Fields.ContainsKey("Manual_Tags"));
    }

    /// <summary>The window announces Library changes on the messenger it is given, not only the app-wide one.</summary>
    [Fact]
    public async Task TheWindow_UsesTheMessengerItIsGiven()
    {
        var messenger = new WeakReferenceMessenger();
        var recipient = new object();
        var sent = 0;
        messenger.Register<object, LibraryChangedMessage>(recipient, (_, _) => sent++);
        var service = Service;
        var vm = new ModFixViewModel(_folder, "Old Mod", service.Check(_folder), service, messenger: messenger);

        await vm.ForgetChoicesCommand.ExecuteAsync(null);

        Assert.Equal(1, sent);
    }

    /// <summary>Regression: the short Mod/Game text showed "+" and quotes as escape codes.</summary>
    [Fact]
    public void Describe_ShowsPlusAndQuotesAsTheyAre()
    {
        var value = new JsonObject { ["(Value=\"X_+%\")"] = 1, ["B"] = 2, ["C"] = 3, ["D"] = 4 };

        Assert.Contains("(Value=\\\"X_+%\\\")", ModFixViewModel.Describe(value));
    }

    /// <summary>Regression: for a recipe list the MOD HAS column showed raw JSON while the panel below showed "Wood ×3".</summary>
    [Fact]
    public void OnlyIn_ARecipeList_NamesTheItemAndCount()
    {
        var mod = JsonNode.Parse("""[{"Element":{"RowName":"Wood"},"Count":3}]""");
        var game = JsonNode.Parse("""[{"Element":{"RowName":"Wood"},"Count":5}]""");

        Assert.Equal("only here: Wood ×3", ModFixViewModel.OnlyIn(mod, game));
    }

    /// <summary>Regression: the group button for changes the game ignores read "Keep the mod's for all", though the fix only leaves them.</summary>
    [Theory]
    [InlineData(ModIssueKind.NoEffect)]
    [InlineData(ModIssueKind.NothingGivesItem)]
    [InlineData(ModIssueKind.RowsInBuiltInTable)]
    public void GroupChoiceLabel_ForProblemsOnlyTheAuthorCanFix_SaysLeaveThemAll(ModIssueKind kind)
    {
        Assert.Equal("Leave them all as they are", ModFixViewModel.GroupChoiceLabel(ModIssueFix.KeepMod, kind));
    }

    /// <summary>The explanations a player reads name no person, tool or date.</summary>
    [Fact]
    public void KindExplanation_NamesNoPersonOrTool()
    {
        foreach (var kind in Enum.GetValues<ModIssueKind>())
        {
            var text = ModFixViewModel.KindExplanation(kind);
            Assert.DoesNotContain("MkHaters", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Hook Scout", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", text);
        }

        Assert.Contains("Tested in game", ModFixViewModel.KindExplanation(ModIssueKind.NoEffect));
        Assert.Contains("Tested in game", ModFixViewModel.KindExplanation(ModIssueKind.RowsInBuiltInTable));
    }

    /// <summary>Notes from the game's code show in their own group, are never counted as problems, and Apply leaves them.</summary>
    [Fact]
    public async Task AGameCodeNote_ShowsInItsOwnGroup_AndIsNoProblem()
    {
        var seeds = Path.Combine(DataFolder, "Farming", "D_FarmingSeeds.json");
        Directory.CreateDirectory(Path.GetDirectoryName(seeds)!);
        File.WriteAllText(seeds, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Wheat","GrowthTime":10}]}""");
        var folderPath = _library.GetFolderPath(_folder);
        var package = ExmodFolder.Read(folderPath).Package;
        package.Rows.RemoveAll(r => r.CurrentFile != "EndOfMod");
        package.Rows.Insert(0, new ExmodFileRow { CurrentFile = "Farming-D_FarmingSeeds.json", FileItems = [new ExmodFileItem { Name = "Grape", Fields = new() { ["GrowthTime"] = 5 } }] });
        ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
        var service = Service;
        var report = service.Check(_folder);
        Assert.DoesNotContain(report.Issues, i => i.IsNote);

        var vm = new ModFixViewModel(_folder, "Old Mod", report, service);

        var group = Assert.Single(vm.Groups);
        Assert.Equal("Notes from the game's code", group.Title);
        Assert.Equal("Read from the game's code, not tested in game: things to know about these changes. Nothing to fix.", group.Explanation);
        var note = Assert.Single(group.Issues);
        Assert.Equal("Leave it as it is", note.SelectedChoice.Label);
        Assert.False(note.ShowValues);
        Assert.Equal(0, vm.ChangeCount);
        Assert.Contains("Nothing to fix", vm.Summary);

        vm.Close = () => { };
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, service.RememberedCount(_folder));
        Assert.Single(service.GameCodeNotes(_folder));
    }

    /// <summary>
    /// Regression: opening the window read the mod again for its notes, with nothing to catch a
    /// file that went bad after the check, so the window failed to open.
    /// </summary>
    [Fact]
    public void Open_ModThatWentBadAfterTheCheck_OpensWithNoNotes()
    {
        var service = Service;
        var report = service.Check(_folder);
        var exmod = Directory.EnumerateFiles(_library.GetFolderPath(_folder), "*.exmod", SearchOption.AllDirectories).Single();
        File.WriteAllText(exmod, "{ not json");

        var vm = new ModFixViewModel(_folder, "Old Mod", report, service);

        Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Issues), i => i.Issue.IsNote);
        Assert.NotEmpty(vm.Groups);
    }

    [Fact]
    public void TableRow_HasNoGameFileHint()
    {
        var rows = Open().Groups.SelectMany(g => g.Issues).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, i => Assert.Null(i.AssetHint));
        Assert.All(rows, i => Assert.Null(i.FieldSubtitle));
    }
}
