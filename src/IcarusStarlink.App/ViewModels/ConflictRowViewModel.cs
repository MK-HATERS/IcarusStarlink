using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One field two or more queued mods touch differently. Options[0] is always "Default (last mod
/// wins)" — SelectedOptionIndex 0 means "no manual pick", matching MergeEngine.Merge's own
/// semantics for a key absent from manualPicks; Options[i+1] corresponds to Conflict.Candidates[i].
/// </summary>
public sealed partial class ConflictRowViewModel : ObservableObject
{
    public ConflictRowViewModel(FieldConflict conflict, int? existingPickIndex)
    {
        Conflict = conflict;
        // EXMOD's own dash convention ("Traits-D_Fuel.json") converted back to the real path, same
        // as every other place this app shows CurrentFile to a user.
        Display = $"{conflict.CurrentFile.Replace('-', '/')} — {conflict.ItemName}.{conflict.FieldName}";

        var lastModName = conflict.Candidates[^1].ModName;
        Options = [
            $"Default (last mod wins: {lastModName})",
            .. conflict.Candidates.Select(c => $"{c.ModName}: {FormatValue(c.Change)}"),
        ];

        _selectedOptionIndex = existingPickIndex.HasValue ? existingPickIndex.Value + 1 : 0;
    }

    public FieldConflict Conflict { get; }
    public string Display { get; }
    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    private int _selectedOptionIndex;

    /// <summary>Null means "Default" is selected — the key should be left out of the manualPicks dictionary entirely, not mapped to some sentinel index.</summary>
    public int? PickedCandidateIndex => SelectedOptionIndex == 0 ? null : SelectedOptionIndex - 1;

    private static string FormatValue(FieldChange change) =>
        change.IsFieldRemoved ? "(removed)" : change.NewValue?.ToJsonString() ?? "null";
}
