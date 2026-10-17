using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One saved mount's plain fields — Name/Level editable, Type shown only. Node is the mount's own
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

    public System.Text.Json.Nodes.JsonObject Node { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _levelText;

    /// <summary>
    /// Shown, never edited (a review finding): MountType is only the label the mount list shows.
    /// The mount's real species, talents and stats live in RecorderBlob, so changing this made a
    /// mount whose list entry said one species while the game still had the other, with that
    /// species' talents.
    /// </summary>
    public string TypeRowName { get; }

    public string TypeDisplayName => SaveGameNames.HumanizeMountType(TypeRowName);

    /// <summary>D_Mounts's own raw "Icon" field for this mount's type (a "/Game/…/T_Talent_Base_Xxx.T_Talent_Base_Xxx" texture reference), resolved once at construction from SaveGameNames.MountTypeIcons. Null when the current data extraction has no Icon for this type.</summary>
    public string? IconPath { get; }

    /// <summary>Null until (if ever) SavesViewModel's own background resolution decodes IconPath through the base-game content provider — a row shows text-only for however long that takes, or forever if it never resolves.</summary>
    [ObservableProperty]
    private BitmapImage? _icon;

    public bool IsDirty =>
        Name != _originalName
        || (int.TryParse(LevelText, out var level) ? level != _originalLevel : LevelText != _originalLevel.ToString());

    public SaveMountViewModel(
        System.Text.Json.Nodes.JsonObject node, string name, int level, string typeRowName,
        string? iconPath, Action onDirtyChanged)
    {
        Node = node;
        _name = name;
        _originalName = name;
        _levelText = level.ToString();
        _originalLevel = level;
        TypeRowName = typeRowName;
        IconPath = iconPath;
        _onDirtyChanged = onDirtyChanged;
    }

    partial void OnNameChanged(string value) => _onDirtyChanged();

    partial void OnLevelTextChanged(string value) => _onDirtyChanged();

    /// <summary>
    /// Writes Name/Level straight into the mount's own live Node — everything else on it
    /// (MountType, RecorderBlob, MountIconName, DatabaseGUID) is never touched. A non-numeric Level
    /// box keeps the original value rather than corrupting the save. Both are what the mount list
    /// shows; RecorderBlob carries its own MountName, Experience and LastLevelAchieved, which the
    /// game may keep using (the page says so).
    /// </summary>
    public void ApplyToNode()
    {
        Node["MountName"] = Name;
        if (int.TryParse(LevelText, out var level) && level >= 0)
        {
            Node["MountLevel"] = level;
        }
    }

    /// <summary>
    /// The Level branch must reject exactly what ApplyToNode() rejects — a negative LevelText that
    /// Save silently declined to write must not be adopted as the new "clean" baseline either, or
    /// IsDirty would compare LevelText against itself and read false, showing "Saved" with the Save
    /// button disabled while MountLevel on disk is actually still whatever it was before the edit.
    /// </summary>
    public void MarkClean()
    {
        _originalName = Name;
        if (int.TryParse(LevelText, out var level) && level >= 0)
        {
            _originalLevel = level;
        }
    }
}
