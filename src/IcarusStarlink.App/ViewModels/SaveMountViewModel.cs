using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One saved mount's plain, safely editable fields — Name/Level/Type. Node is the mount's own
/// real JsonObject (from Mounts.json's SavedMounts array), kept and mutated in place at Apply time
/// rather than rebuilt: a mount also carries RecorderBlob (a raw Unreal binary-serialized blob —
/// confirmed against a real save, not JSON — holding its actual stats/stomach/saddle state) and
/// MountIconName/DatabaseGUID, none of which this editor understands or touches. Unlike Bestiary/
/// Accolade entries, a mount has no natural unique key to rebuild an array by (DatabaseGUID is
/// literally "noguid" on every real entry observed, and a player can own two mounts of the same
/// MountType) — holding the live node sidesteps that entirely.
/// </summary>
public sealed partial class SaveMountViewModel : ObservableObject, IDirtyTrackable
{
    private readonly Action _onDirtyChanged;
    private string _originalName;
    private int _originalLevel;
    private string _originalTypeRowName;

    public System.Text.Json.Nodes.JsonObject Node { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _levelText;

    [ObservableProperty]
    private string _typeRowName;

    public string TypeDisplayName => SaveGameNames.HumanizeMountType(TypeRowName);

    /// <summary>Every real MountType value currently known (D_Mounts's own row names) — the picker's own item source, so editing Type can only ever land on a real, valid species.</summary>
    public IReadOnlyList<string> AvailableTypeRowNames { get; }

    public bool IsDirty =>
        Name != _originalName
        || (int.TryParse(LevelText, out var level) ? level != _originalLevel : LevelText != _originalLevel.ToString())
        || TypeRowName != _originalTypeRowName;

    public SaveMountViewModel(
        System.Text.Json.Nodes.JsonObject node, string name, int level, string typeRowName,
        IReadOnlyList<string> availableTypeRowNames, Action onDirtyChanged)
    {
        Node = node;
        _name = name;
        _originalName = name;
        _levelText = level.ToString();
        _originalLevel = level;
        _typeRowName = typeRowName;
        _originalTypeRowName = typeRowName;
        AvailableTypeRowNames = availableTypeRowNames;
        _onDirtyChanged = onDirtyChanged;
    }

    partial void OnNameChanged(string value) => _onDirtyChanged();

    partial void OnLevelTextChanged(string value) => _onDirtyChanged();

    partial void OnTypeRowNameChanged(string value)
    {
        OnPropertyChanged(nameof(TypeDisplayName));
        _onDirtyChanged();
    }

    /// <summary>Writes Name/Level/Type straight into the mount's own live Node — everything else on it (RecorderBlob, MountIconName, DatabaseGUID) is simply never touched. A non-numeric Level box keeps the original value rather than corrupting the save.</summary>
    public void ApplyToNode()
    {
        Node["MountName"] = Name;
        if (int.TryParse(LevelText, out var level) && level >= 0)
        {
            Node["MountLevel"] = level;
        }

        Node["MountType"] = TypeRowName;
    }

    public void MarkClean()
    {
        _originalName = Name;
        if (int.TryParse(LevelText, out var level))
        {
            _originalLevel = level;
        }

        _originalTypeRowName = TypeRowName;
    }
}
