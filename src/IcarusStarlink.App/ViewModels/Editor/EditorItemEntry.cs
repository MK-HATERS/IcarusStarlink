using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One game item in the Editor page's list: its names, its icon (asked for when the row first shows), and whether the mod being saved to already changes it.</summary>
public sealed partial class EditorItemEntry(GameItemInfo info, GameIconLoader? icons) : ObservableObject
{
    private bool _iconAsked;

    public GameItemInfo Info { get; } = info;

    public string Name => Info.Name;

    public string Title => Info.Item.Title;

    public string? Subtitle => Info.Item.Subtitle;

    public string Display => Info.Item.Display;

    public string Category => Info.Category;

    /// <summary>The list's tooltip: both names, then what the game says about it.</summary>
    public string ToolTipText => Info.Description is { Length: > 0 } d ? $"{Display}\n{Info.Category}\n\n{d}" : $"{Display}\n{Info.Category}";

    private ImageSource? _icon;

    /// <summary>The item's in-game icon; null until it's decoded (or when it has none). Reading it the first time asks for it, so only rows that show get decoded.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconAsked && icons is not null && Info.IconPath is { } path)
            {
                _iconAsked = true;
                icons.Request(path, image => { _icon = image; OnPropertyChanged(nameof(Icon)); }, () => _iconAsked = false);
            }

            return _icon;
        }
    }

    /// <summary>The mod chosen under Save to already changes this item (a dot in the list).</summary>
    [ObservableProperty]
    private bool _isInTargetMod;

    /// <summary>Another turned-on mod changes one of this item's rows (a ◆ in the list).</summary>
    [ObservableProperty]
    private bool _isChangedByOtherMods;

    /// <summary>Ticked in the list for a bulk edit.</summary>
    [ObservableProperty]
    private bool _isTicked;

    /// <summary>Changes typed for this item and not saved yet (a pencil in the list).</summary>
    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>What a screen reader says for the item (the list's text otherwise).</summary>
    public override string ToString() => Display;
}
