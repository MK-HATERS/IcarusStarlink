using System.IO;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class ExmodEditorViewModelTests
{
    /// <summary>
    /// Regression: Ctrl+S wrote the mod and then failed on its locked Library file, so it said
    /// "Save failed" for a save that had happened, and the next Save was refused as "changed
    /// somewhere else".
    /// </summary>
    [Fact]
    public void Save_WhenTheModsLibraryFileIsLocked_Fails_AndTheNextSaveGoesThrough()
    {
        var editor = OpenMod();
        editor.SelectedItem = editor.Items.Single();
        editor.Fields.Single(f => f.FieldName == "Weight").ValueText = "25";
        var folder = _library.GetAll().Single(e => e.Name == "Light suit").FolderName;
        var exmod = Directory.GetFiles(_library.GetFolderPath(folder), "*.EXMOD", SearchOption.AllDirectories).Single();
        var before = File.ReadAllText(exmod);

        using (new FileStream(Path.Combine(_root, "Library_Meta", folder + ".json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            editor.SaveCommand.Execute(null);
        }

        Assert.StartsWith("Save failed", editor.StatusMessage);
        Assert.Equal(before, File.ReadAllText(exmod));

        editor.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", editor.StatusMessage);
        editor.Detach();
    }
}
