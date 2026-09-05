using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Compare;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Transient, per-open-instance ViewModel for PakCompareWindow — same shape as
/// ConflictPickerViewModel (only ever opened from MergeInstallViewModel, so constructed directly
/// rather than through a DI factory). Built for the "verify classic IMM's merged pak and this
/// app's own rebuilt pak are equivalent" migration workflow, but compares any two paks.
/// </summary>
public sealed partial class PakCompareViewModel : ObservableObject
{
    private readonly IPakCompareService _compareService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _firstPakPath = "";

    [ObservableProperty]
    private string _secondPakPath = "";

    [ObservableProperty]
    private bool _isComparing;

    [ObservableProperty]
    private string? _summaryMessage;

    [ObservableProperty]
    private ChangedDataFileViewModel? _selectedFile;

    /// <summary>Reuses Weekly Changes' own per-file projection — both present "two versions of the same DataTable JSON" the same way. Here "New"/"new item" reads as "only in the second pak", "Removed" as "only in the first".</summary>
    public ObservableCollection<ChangedDataFileViewModel> ChangedFiles { get; } = [];

    public ObservableCollection<string> AssetDifferences { get; } = [];

    [ObservableProperty]
    private bool _hasAssetDifferences;

    public PakCompareViewModel(IPakCompareService compareService, ISettingsService settingsService, string defaultFirstPakPath)
    {
        _compareService = compareService;
        _settingsService = settingsService;

        // The most common first side is this app's own staged build — prefilled only when it
        // actually exists, so a fresh install doesn't open with a dead path.
        if (File.Exists(defaultFirstPakPath))
        {
            FirstPakPath = defaultFirstPakPath;
        }
    }

    [RelayCommand]
    private void BrowseFirstPak()
    {
        if (PickPakFile() is { } path)
        {
            FirstPakPath = path;
        }
    }

    [RelayCommand]
    private void BrowseSecondPak()
    {
        if (PickPakFile() is { } path)
        {
            SecondPakPath = path;
        }
    }

    private static string? PickPakFile()
    {
        var dialog = new OpenFileDialog { Filter = "Pak files (*.pak)|*.pak|All files (*.*)|*.*" };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CompareAsync()
    {
        if (!File.Exists(FirstPakPath) || !File.Exists(SecondPakPath))
        {
            SummaryMessage = "Pick two existing .pak files to compare.";
            return;
        }

        var unrealPakExePath = _settingsService.Current.UnrealPakExePath;
        if (string.IsNullOrWhiteSpace(unrealPakExePath))
        {
            SummaryMessage = "Set UnrealPak.exe path in Settings first.";
            return;
        }

        IsComparing = true;
        SummaryMessage = "Extracting and comparing…";
        ChangedFiles.Clear();
        AssetDifferences.Clear();
        SelectedFile = null;
        HasAssetDifferences = false;

        try
        {
            var result = await _compareService.CompareAsync(unrealPakExePath, FirstPakPath, SecondPakPath);

            foreach (var file in result.DataDifferences)
            {
                ChangedFiles.Add(new ChangedDataFileViewModel(file));
            }

            foreach (var asset in result.AssetDifferences)
            {
                AssetDifferences.Add($"{asset.RelativePath} — {DescribeKind(asset.Kind)}");
            }

            HasAssetDifferences = AssetDifferences.Count > 0;

            SummaryMessage = result.DataDifferences.Count == 0 && result.AssetDifferences.Count == 0
                ? $"The two paks are equivalent — every DataTable row and every asset matches. ({result.FirstFileCount} file(s) in the first pak, {result.SecondFileCount} in the second.)"
                : $"{result.DataDifferences.Count} DataTable file(s) differ, {result.AssetDifferences.Count} other file(s) differ. ({result.FirstFileCount} file(s) in the first pak, {result.SecondFileCount} in the second.)";
        }
        catch (Exception ex)
        {
            SummaryMessage = $"Compare failed: {ex.Message}";
        }
        finally
        {
            IsComparing = false;
        }
    }

    private static string DescribeKind(PakAssetDifferenceKind kind) => kind switch
    {
        PakAssetDifferenceKind.OnlyInFirst => "only in the first pak",
        PakAssetDifferenceKind.OnlyInSecond => "only in the second pak",
        _ => "content differs",
    };
}
