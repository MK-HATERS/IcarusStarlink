using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One accolade row — same deferred-apply toggle pattern as SaveFlagViewModel (the save stores completed accolades as a list of RowName references, not a per-row bool, but the editing UX is identical: checked = present in that list).</summary>
public sealed partial class SaveAccoladeViewModel : DirtyRow
{
    private bool _originalCompleted;

    public string RowName { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string Category { get; }

    [ObservableProperty]
    private bool _isCompleted;

    public override bool IsDirty => IsCompleted != _originalCompleted;

    public SaveAccoladeViewModel(string rowName, string displayName, string description, string category, bool isCompleted, Action onDirtyChanged)
        : base(onDirtyChanged)
    {
        RowName = rowName;
        DisplayName = displayName;
        Description = description;
        Category = category;
        _isCompleted = isCompleted;
        _originalCompleted = isCompleted;
    }

    partial void OnIsCompletedChanged(bool value) => Changed();

    protected override void Snapshot() => _originalCompleted = IsCompleted;
}
