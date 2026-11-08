using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Mod details: the mod's name, author, version, description and links, set in the editor.</summary>
public sealed partial class ExmodEditorViewModelTests
{
    private string FolderOf(string name) => _library.GetAll().Single(e => e.Name == name).FolderName;

    private ExmodPackage ReadSaved(string folder) => ExmodFolder.ReadPackageOnly(_library.GetFolderPath(folder));

    [Fact]
    public void ModDetails_ShowTheModsOwnValues()
    {
        var editor = OpenMod();

        Assert.Equal("Light suit", editor.ModName);
        Assert.Equal("Me", editor.ModAuthor);
        editor.Detach();
    }

    [Fact]
    public void ModDetails_Save_WritesThemIntoTheExmod()
    {
        var editor = OpenMod();
        var folder = FolderOf("Light suit");

        editor.ModVersion = "1.2";
        editor.ModAuthor = "MkHaters";
        editor.ModDescription = "Halves the suit's weight.";
        editor.ModImageUrl = "https://example.com/a.png";
        editor.ModReadmeUrl = "https://example.com/readme";
        Assert.True(editor.HasUnsavedChanges);
        editor.SaveCommand.Execute(null);

        Assert.StartsWith("Saved", editor.StatusMessage);
        var saved = ReadSaved(folder);
        Assert.Equal("1.2", saved.Version);
        Assert.Equal("MkHaters", saved.Author);
        Assert.Equal("Halves the suit's weight.", saved.Description);
        Assert.Equal("https://example.com/a.png", saved.ImageUrl);
        Assert.Equal("https://example.com/readme", saved.ReadmeUrl);
        Assert.False(editor.HasUnsavedChanges);
        editor.Detach();
    }

    [Fact]
    public void ModDetails_Undo_PutsTheOldValueBack_AndRedoTheNewOne()
    {
        var editor = OpenMod();
        var before = editor.ModVersion;
        var raised = new List<string?>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        editor.ModVersion = "2";
        editor.ModVersion = "2.0"; // more typing in the same box is the same change
        editor.UndoCommand.Execute(null);

        Assert.Equal(before, editor.ModVersion);
        Assert.Contains(nameof(ExmodEditorViewModel.ModVersion), raised);
        Assert.False(editor.HasUnsavedChanges);

        editor.RedoCommand.Execute(null);
        Assert.Equal("2.0", editor.ModVersion);
        editor.Detach();
    }

    [Fact]
    public void ModDetails_TypingInAnotherBox_IsItsOwnUndoStep()
    {
        var editor = OpenMod();

        editor.ModVersion = "3";
        editor.ModAuthor = "Someone";
        editor.UndoCommand.Execute(null);

        Assert.Equal("3", editor.ModVersion);
        Assert.Equal("Me", editor.ModAuthor);
        editor.Detach();
    }

    [Fact]
    public void ModDetails_NewName_ShowsInTheTitle_AndLibrary_AfterSave()
    {
        var editor = OpenMod();
        var folder = FolderOf("Light suit");

        editor.ModName = "Lighter suit";
        Assert.Contains("Lighter suit", editor.WindowTitle);
        editor.SaveCommand.Execute(null);

        Assert.Equal("Lighter suit", ReadSaved(folder).Name);
        Assert.Equal("Lighter suit", _library.GetAll().Single(e => e.FolderName == folder).Name);
        editor.Detach();
    }

    [Fact]
    public void ModDetails_BlankName_IsNotSaved()
    {
        var editor = OpenMod();
        var folder = FolderOf("Light suit");

        editor.ModName = "   ";
        editor.SaveCommand.Execute(null);

        Assert.StartsWith("Not saved", editor.StatusMessage);
        Assert.Equal("Light suit", ReadSaved(folder).Name);
        editor.Detach();
    }

    /// <summary>A rename in Library wins over the mod's own name, so the editor says the Library keeps showing the rename.</summary>
    [Fact]
    public void ModDetails_ModRenamedInLibrary_SaysLibraryKeepsTheRename()
    {
        var editor = OpenMod();
        Assert.Null(editor.ModNameNote);
        editor.Detach();

        _library.SetDisplayNameOverride(FolderOf("Light suit"), "My suit");
        var renamed = new ExmodEditorViewModel(FolderOf("My suit"), _library, DataFolder, new GameDataIndexCache());

        Assert.Contains("My suit", renamed.ModNameNote);
        renamed.Detach();
    }

    /// <summary>A downloaded mod's name and author are what its updates are found by.</summary>
    [Fact]
    public void ModDetails_SomeoneElsesMod_WarnsThatANewNameCanLoseItsUpdates()
    {
        var mine = OpenMod();
        Assert.Null(mine.ModDetailsWarning);
        mine.Detach();

        var source = Path.Combine(_root, "_download");
        ExmodFolder.Write(source, new ExmodPackageContents(
            new ExmodPackage { Name = "From Nexus", Author = "Someone", Version = "1", Description = "", FileName = "From_Nexus", Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }] }, []));
        var folder = _library.Import(source, source: "Nexus", nexusModId: 42).FolderName;
        var editor = new ExmodEditorViewModel(folder, _library, DataFolder, new GameDataIndexCache());

        Assert.Contains("updates", editor.ModDetailsWarning);
        editor.Detach();
    }
}
