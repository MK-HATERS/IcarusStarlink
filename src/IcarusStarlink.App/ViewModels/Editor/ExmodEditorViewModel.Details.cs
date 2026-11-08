using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Mod details: the .EXMOD's own name, author, version, description and links, which nothing else
/// in the app sets (a modder getting a mod ready for Nexus would otherwise edit the file by hand).
/// Each box writes into the package as it is typed; the first write to a box after anything else
/// is one Undo step, like a field's typing. FileName is never changed here.
/// </summary>
public sealed partial class ExmodEditorViewModel
{
    /// <summary>The undo key's file part for these boxes: no data file is named this, so it never joins a row field's typing.</summary>
    private const string DetailsKey = "<mod details>";

    public string ModName
    {
        get => _package.Name;
        set => SetDetail(_package.Name, value, v => _package.Name = v, nameof(ModName));
    }

    public string ModAuthor
    {
        get => _package.Author;
        set => SetDetail(_package.Author, value, v => _package.Author = v, nameof(ModAuthor));
    }

    public string ModVersion
    {
        get => _package.Version;
        set => SetDetail(_package.Version, value, v => _package.Version = v, nameof(ModVersion));
    }

    public string ModDescription
    {
        get => _package.Description;
        set => SetDetail(_package.Description, value, v => _package.Description = v, nameof(ModDescription));
    }

    public string ModImageUrl
    {
        get => _package.ImageUrl ?? "";
        set => SetDetail(_package.ImageUrl ?? "", value, v => _package.ImageUrl = v, nameof(ModImageUrl));
    }

    public string ModReadmeUrl
    {
        get => _package.ReadmeUrl ?? "";
        set => SetDetail(_package.ReadmeUrl ?? "", value, v => _package.ReadmeUrl = v, nameof(ModReadmeUrl));
    }

    /// <summary>Set when Library shows this mod under a rename of the user's: that rename wins over the name typed here.</summary>
    public string? ModNameNote => LibraryEntryOfThisMod()?.DisplayNameOverride is { } rename
        ? $"Library shows this mod as '{rename}', the name you gave it there. To show this name instead, rename it in Library and click Reset to default."
        : null;

    /// <summary>Set for a mod that isn't the user's own: its updates are matched partly by its name and author.</summary>
    public string? ModDetailsWarning => LibraryEntryOfThisMod() is { IsOwnMod: false }
        ? "This mod was downloaded or imported, not made here. A new name or author can stop Starlink from finding its updates."
        : null;

    private LibraryEntry? LibraryEntryOfThisMod() =>
        _repository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, _folderName, StringComparison.OrdinalIgnoreCase));

    private void SetDetail(string current, string? value, Action<string> write, string key)
    {
        value ??= "";
        if (value == current)
        {
            return;
        }

        SnapshotBeforeFieldEdit(DetailsKey, "", key);
        write(value);
        OnPropertyChanged(key);
        if (key == nameof(ModName))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    /// <summary>After Undo/Redo put a whole package back: every box shows its value again.</summary>
    private void RaiseModDetailsChanged()
    {
        OnPropertyChanged(nameof(ModName));
        OnPropertyChanged(nameof(ModAuthor));
        OnPropertyChanged(nameof(ModVersion));
        OnPropertyChanged(nameof(ModDescription));
        OnPropertyChanged(nameof(ModImageUrl));
        OnPropertyChanged(nameof(ModReadmeUrl));
    }
}
