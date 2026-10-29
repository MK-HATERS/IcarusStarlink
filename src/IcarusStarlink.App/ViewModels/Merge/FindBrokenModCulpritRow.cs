using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A mod the search found (or one it could still be).</summary>
public sealed partial class FindBrokenModCulpritRow(string folderName, string name, string note, bool hasProblems, int? nexusModId) : ObservableObject
{
    public string FolderName { get; } = folderName;

    public string Name { get; } = name;

    public string Note { get; } = note;

    public bool HasNote => Note.Length > 0;

    public bool HasProblems { get; } = hasProblems;

    public int? NexusModId { get; } = nexusModId;

    public bool HasNexusPage => NexusModId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTurnOff))]
    private bool _isTurnedOff;

    public bool CanTurnOff => !IsTurnedOff;
}
