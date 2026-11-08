using System.IO;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class GameEditorViewModelTests
{
    /// <summary>
    /// Regression: the mod was written before its Library file was marked, so a locked Library file
    /// said "Couldn't save" after the save had happened, and the next Save was refused as "changed
    /// somewhere else".
    /// </summary>
    [Fact]
    public void Save_WhenTheModsSettingsFileIsLocked_WritesNothing_AndTheNextSaveGoesThrough()
    {
        var mine = _library.CreateBlankMod("Mine", "Me");
        var vm = Open();
        vm.SelectedTarget = vm.Targets.Single(t => t.FolderName == mine.FolderName);
        vm.SelectedItem = vm.FilteredItems.Single(i => i.Name == "Envirosuit");
        Weight(vm).ValueText = "50";

        using (new FileStream(Path.Combine(_root, "Library_Meta", mine.FolderName + ".json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            vm.SaveCommand.Execute(null);
        }

        Assert.StartsWith("Couldn't save", vm.StatusMessage);
        Assert.DoesNotContain("Item_Envirosuit", ExmodJson.Serialize(Read(_library, mine.FolderName)));

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", vm.StatusMessage);
        Assert.Contains("Item_Envirosuit", ExmodJson.Serialize(Read(_library, mine.FolderName)));
    }
}
