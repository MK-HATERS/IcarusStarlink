using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One unlockable flag chip — the save stores just the int ID; the name comes from the game's own flag table (row index = ID). Toggling stays in the ViewModel until save-time apply, like every other save-editor edit.</summary>
public sealed partial class SaveFlagViewModel : DirtyRow
{
    private bool _originalUnlocked;

    public int Id { get; }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLearnedNote))]
    private bool _isUnlocked;

    /// <summary>
    /// "MKHATERS already learned Caveworm Knife, Caveworm Spear" — set by SavesViewModel for a mission
    /// reward whose blueprints the selected character has although the flag is off (the game shows
    /// those as unlocked). Null when there's nothing to say.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLearnedNote))]
    private string? _learnedNote;

    public bool ShowLearnedNote => LearnedNote is not null && !IsUnlocked;

    /// <summary>"Switching it on marks Recovery done too" — a mission reward linked to its mission row.</summary>
    [ObservableProperty]
    private string? _linkHint;

    /// <summary>Show the link hint on the row itself, not just its tooltip: a reward several missions give, where switching it on marks none (the one-mission case is said once in the section's description).</summary>
    [ObservableProperty]
    private bool _showLinkHint;

    public override bool IsDirty => IsUnlocked != _originalUnlocked;

    /// <summary>What the flag does / where it comes from (FlagInfo.Detail); empty for DLC packs.</summary>
    public string Detail { get; }

    public FlagKind Kind { get; }

    /// <summary>The game's own row name, for the tooltip.</summary>
    public string RowName { get; }

    /// <summary>Talent-granted flags follow their talent; editing one alone is pointless, so it's shown, not switched.</summary>
    public bool IsEditable => Kind != FlagKind.FromTalent;

    public SaveFlagViewModel(int id, string name, bool isUnlocked, Action onDirtyChanged)
        : this(id, new FlagInfo(name, name, "", FlagKind.Marker), isUnlocked, onDirtyChanged)
    {
    }

    public SaveFlagViewModel(int id, FlagInfo info, bool isUnlocked, Action onDirtyChanged)
        : base(onDirtyChanged)
    {
        Id = id;
        Name = info.Display;
        Detail = info.Detail;
        Kind = info.Kind;
        RowName = info.RowName;
        _isUnlocked = isUnlocked;
        _originalUnlocked = isUnlocked;
    }

    partial void OnIsUnlockedChanged(bool value) => Changed();

    protected override void Snapshot() => _originalUnlocked = IsUnlocked;
}
