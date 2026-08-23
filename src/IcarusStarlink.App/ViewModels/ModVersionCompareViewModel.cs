using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.PakIO.Compare;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// "What did the author change?" — a read-only view of one mod's differences between two versions
/// (its pre-update backup vs what's installed now). Transient, constructed directly by
/// LibraryViewModel, same as ConflictPickerViewModel/PakCompareViewModel.
/// </summary>
public sealed partial class ModVersionCompareViewModel : ObservableObject
{
    public string ModName { get; }

    public string SummaryMessage { get; }

    /// <summary>Same per-file projection Weekly Changes and the pak comparison use — "new item" here means the newer version added it, "row removed" that it dropped it.</summary>
    public ObservableCollection<ChangedDataFileViewModel> ChangedFiles { get; } = [];

    public ObservableCollection<string> AssetDifferences { get; } = [];

    public bool HasAssetDifferences => AssetDifferences.Count > 0;

    [ObservableProperty]
    private ChangedDataFileViewModel? _selectedFile;

    public ModVersionCompareViewModel(string modName, ModVersionCompareResult result)
    {
        ModName = modName;

        foreach (var file in result.DataDifferences)
        {
            ChangedFiles.Add(new ChangedDataFileViewModel(file));
        }

        foreach (var asset in result.AssetDifferences)
        {
            AssetDifferences.Add($"{asset.RelativePath} — {DescribeKind(asset.Kind)}");
        }

        SelectedFile = ChangedFiles.FirstOrDefault();

        var changedItemCount = result.DataDifferences.Sum(f => f.FieldChanges.Count + f.RemovedRowNames.Count);
        SummaryMessage = result.IsIdentical
            ? $"No differences between {result.OldLabel} and {result.NewLabel} — the author changed nothing this app can see."
            : $"{result.OldLabel} → {result.NewLabel}: {changedItemCount} change(s) across {result.DataDifferences.Count} game file(s)"
              + (result.AssetDifferences.Count > 0 ? $", plus {result.AssetDifferences.Count} changed asset file(s)." : ".");
    }

    private static string DescribeKind(PakAssetDifferenceKind kind) => kind switch
    {
        PakAssetDifferenceKind.OnlyInFirst => "removed in the new version",
        PakAssetDifferenceKind.OnlyInSecond => "added in the new version",
        _ => "content changed",
    };
}
