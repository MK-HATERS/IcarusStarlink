using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Saves;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Talent tree icons and Undo, row icon size, and the picked player-data backup.</summary>
public sealed partial class SavesViewModelTests
{
    /// <summary>CreateTalentTreeData plus one mission map (Olympus) with two missions that have pictures.</summary>
    private string CreateTalentTreeDataWithMissions()
    {
        var data = CreateTalentTreeData();
        void AddRows(string table, string rows)
        {
            var path = Path.Combine(data, "Talents", table);
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (var row in JsonNode.Parse(rows)!.AsArray().ToList())
            {
                row!.Parent!.AsArray().Remove(row);
                root["Rows"]!.AsArray().Add(row);
            }

            File.WriteAllText(path, root.ToJsonString());
        }

        AddRows("D_TalentArchetypes.json", """
            [ { "Name": "Prospect_Olympus", "Model": { "RowName": "Prospect" }, "DisplayName": "NSLOCTEXT(\"a\", \"b\", \"Olympus\")" } ]
            """);
        AddRows("D_TalentTrees.json", """
            [ { "Name": "Prospect_Olympus", "Archetype": { "RowName": "Prospect_Olympus" } } ]
            """);
        AddRows("D_Talents.json", """
            [ { "Name": "Prospect_OLY_First", "TalentTree": { "RowName": "Prospect_Olympus" }, "Icon": "/Game/UI/Talents/T_M.T_M",
                "Position": { "X": 0, "Y": 0 }, "Size": { "X": 200, "Y": 200 } },
              { "Name": "Prospect_OLY_Second", "TalentTree": { "RowName": "Prospect_Olympus" }, "Icon": "/Game/UI/Talents/T_M.T_M",
                "Position": { "X": 600, "Y": 0 }, "Size": { "X": 200, "Y": 200 }, "RequiredTalents": [ { "RowName": "Prospect_OLY_First" } ] } ]
            """);
        return data;
    }

    /// <summary>
    /// Regression: the talent, mission, mount and workshop trees shared one icon queue, so a slot
    /// load (which picks a tree in each in turn) left only the last one's icons loading: the
    /// mission map's nodes showed names and no pictures until another map was picked and back.
    /// </summary>
    [Fact]
    public async Task EveryTreeView_GetsItsIcons_AfterALoad()
    {
        var character = MakeCharacter("Alpha", 0);
        character["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Talent_A", ["Rank"] = 1 });
        var repository = new FakeSaveRepository { Characters = [character], Profile = MakeProfile(nextChrSlot: 1) };
        // The gate holds every decode until the load is done, so the queues can't finish early by luck.
        var decoder = new FakeBaseGameIconDecoder { PngBytesToReturn = MinimalPng, Gate = new ManualResetEventSlim() };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateTalentTreeDataWithMissions()), baseGameIconDecoder: decoder);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var missions = Assert.Single(vm.MissionTrees.Trees);
            Assert.Same(missions, vm.MissionTrees.SelectedTree);

            decoder.Gate.Set();
            await vm.WaitForTalentIconsAsync();

            Assert.Equal(2, missions.Talents.Count);
            Assert.All(missions.Talents, t => Assert.NotNull(t.Icon));
            Assert.NotNull(vm.Characters.Single().Talents.Single(t => t.RowName == "Talent_A").Icon);
        }
        finally
        {
            decoder.Gate.Set();
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>A 512 px PNG made here.</summary>
    private static byte[] BigPng()
    {
        var source = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Bgra32, null, new byte[512 * 512 * 4], 512 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Regression: row icons (shown at 30-52 px) were kept at the texture's full size, often 512 px.</summary>
    [Fact]
    public async Task RowIcons_AreDecodedSmall()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            MetaInventory = MakeMetaInventoryRoot(MakeMetaInventoryItem("ItemStatic_Fiber")),
        };
        var decoder = new FakeBaseGameIconDecoder { PngBytesToReturn = BigPng() };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateGameDataFolder()), baseGameIconDecoder: decoder);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForPendingIconLoadAsync();

            var item = vm.MetaInventoryItems.Single();
            Assert.NotNull(item.Icon);
            Assert.True(((BitmapSource)item.Icon!).PixelWidth <= 128, $"The icon is {((BitmapSource)item.Icon!).PixelWidth} px wide.");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: after Save, a tree's Undo changes stayed on (it only heard rank changes, and Save
    /// only marks rows clean); and undoing a tree told the page once per row it put back.
    /// </summary>
    [Fact]
    public async Task TreeUndo_TurnsOffAfterSave_AndUndoTellsThePageOnce()
    {
        var character = MakeCharacter("Alpha", 0);
        character["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Talent_A", ["Rank"] = 1 });
        var repository = new FakeSaveRepository { Characters = [character], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateTalentTreeData()));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            vm.SelectedCharacter = vm.Characters.Single();
            var tree = vm.TalentTrees.SelectedTree!;
            var treeToldUndo = 0;
            tree.PropertyChanged += (_, e) => treeToldUndo += e.PropertyName == nameof(TalentTreeViewModel.HasChanges) ? 1 : 0;

            vm.MaxTalentGroupCommand.Execute(tree.Group);
            Assert.True(tree.HasChanges);
            treeToldUndo = 0;
            vm.SaveChangesCommand.Execute(null);

            Assert.False(tree.HasChanges);
            Assert.True(treeToldUndo > 0, "Undo changes was never told to look again after Save.");

            vm.ResetTalentGroupCommand.Execute(tree.Group);
            Assert.Equal(2, tree.Talents.Count(t => t.IsDirty));
            var pageToldSummary = 0;
            vm.PropertyChanged += (_, e) => pageToldSummary += e.PropertyName == nameof(SavesViewModel.ChangesSummary) ? 1 : 0;

            vm.TalentTrees.UndoTreeCommand.Execute(tree);

            Assert.False(tree.HasChanges);
            Assert.Equal([3, 1], tree.Talents.OrderBy(t => t.RowName).Select(t => t.Rank));
            Assert.Equal("4 points in this tree: every row open", tree.PointsNote);
            Assert.Equal(1, pageToldSummary);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: a backup picked in Backup &amp; restore jumped back to the newest whenever the page
    /// read the slot again. A backup that wasn't listed before (Backup now, Save) is still selected.
    /// </summary>
    [Fact]
    public async Task PickedBackup_StaysPickedAcrossAReload_ButANewBackupIsSelected()
    {
        var now = DateTimeOffset.Now;
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Backups = [new("newer.zip", now.AddHours(-1)), new("older.zip", now.AddHours(-2))],
        };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            Assert.Equal("newer.zip", vm.SelectedBackup?.FilePath);

            vm.SelectedBackup = vm.Backups.Single(b => b.FilePath == "older.zip");
            vm.ReloadCommand.Execute(null);
            await vm.WaitForPendingSlotLoadAsync();
            Assert.Equal("older.zip", vm.SelectedBackup?.FilePath);

            // The game saved meanwhile: the page reads the slot again when it's shown.
            repository.Stamps["Profile.json"] = new SaveFileStamp(DateTime.UtcNow, 200);
            vm.OnPageShown();
            await vm.WaitForPendingSlotLoadAsync();
            Assert.Equal("older.zip", vm.SelectedBackup?.FilePath);

            repository.Backups.Insert(0, new SaveBackupInfo("manual.zip", now));
            vm.BackupNowCommand.Execute(null);
            Assert.Equal("manual.zip", vm.SelectedBackup?.FilePath);

            // The picked one was pruned: the newest is picked instead.
            vm.SelectedBackup = vm.Backups.Single(b => b.FilePath == "older.zip");
            repository.Backups.RemoveAll(b => b.FilePath == "older.zip");
            vm.ReloadCommand.Execute(null);
            await vm.WaitForPendingSlotLoadAsync();
            Assert.Equal("manual.zip", vm.SelectedBackup?.FilePath);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }
}
