using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Rebuild;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// 6.1 of Phase 6 (see the plan's "Update (2026-08-21)" section): the merge queue + Rebuild
/// engine. Deliberately scoped to producing a correct staged pak under IcarusStarlink's own
/// folder — installing it into the real game's Content\Paks\mods is 6.2's job, gated on the
/// user's explicit go-ahead each time since it overwrites their real installed mod pack.
/// </summary>
public sealed partial class MergeInstallViewModel : ObservableObject
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IRebuildService _rebuildService;
    private readonly ISettingsService _settingsService;
    private readonly string _dataFolder;
    private readonly string _outputPakPath;

    public string Title => "Merge & Install";

    /// <summary>Each element is either a LibraryGroup (a real family) or a bare LibraryEntry (standalone) — same type-per-DataTemplate routing Library's own RootItems already uses.</summary>
    public ObservableCollection<object> LibraryRootItems { get; } = [];

    public ObservableCollection<LibraryEntry> Queue { get; } = [];

    [ObservableProperty]
    private LibraryEntry? _selectedLibraryItem;

    [ObservableProperty]
    private LibraryEntry? _selectedQueueEntry;

    [ObservableProperty]
    private bool _isRebuilding;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<string> Warnings { get; } = [];

    public MergeInstallViewModel(
        ILibraryRepository libraryRepository, IRebuildService rebuildService, ISettingsService settingsService,
        string dataFolder, string outputPakPath)
    {
        _libraryRepository = libraryRepository;
        _rebuildService = rebuildService;
        _settingsService = settingsService;
        _dataFolder = dataFolder;
        _outputPakPath = outputPakPath;

        ReloadLibrary();
    }

    /// <summary>
    /// Opaque .pak entries (LibraryEntry.IsOpaquePak) are excluded here — they have no .EXMOD to
    /// merge in. The spec treats a prebuilt pak as a sidecar copied alongside the merged pack on
    /// install instead (6.2's job), not something Rebuild folds in.
    /// </summary>
    private void ReloadLibrary()
    {
        var groups = VariantGrouping.Group(_libraryRepository.GetAll().Where(e => !e.IsOpaquePak))
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase);

        LibraryRootItems.Clear();
        foreach (var group in groups)
        {
            LibraryRootItems.Add(group.IsFamily ? group : group.Entries[0]);
        }
    }

    [RelayCommand]
    private void AddToQueue()
    {
        if (SelectedLibraryItem is not { } entry)
        {
            return;
        }

        if (Queue.Any(q => q.FolderName == entry.FolderName))
        {
            StatusMessage = $"'{entry.Name}' is already in the queue.";
            return;
        }

        Queue.Add(entry);
    }

    [RelayCommand]
    private void RemoveFromQueue()
    {
        if (SelectedQueueEntry is { } entry)
        {
            Queue.Remove(entry);
        }
    }

    [RelayCommand]
    private void ClearQueue() => Queue.Clear();

    [RelayCommand]
    private void MoveQueueEntryUp()
    {
        if (SelectedQueueEntry is not { } entry)
        {
            return;
        }

        var index = Queue.IndexOf(entry);
        if (index > 0)
        {
            Queue.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveQueueEntryDown()
    {
        if (SelectedQueueEntry is not { } entry)
        {
            return;
        }

        var index = Queue.IndexOf(entry);
        if (index >= 0 && index < Queue.Count - 1)
        {
            Queue.Move(index, index + 1);
        }
    }

    [RelayCommand]
    private async Task RebuildAsync()
    {
        if (Queue.Count == 0)
        {
            StatusMessage = "Add at least one mod to the queue first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_settingsService.Current.UnrealPakExePath))
        {
            StatusMessage = "Set UnrealPak.exe path in Settings first.";
            return;
        }

        IsRebuilding = true;
        StatusMessage = "Rebuilding…";
        Warnings.Clear();

        try
        {
            // Queue order = merge priority (index 0 lowest, matching MergeEngine's own
            // convention) — read fresh from disk each Rebuild rather than caching, so an edit
            // made outside the app (or via the EXMOD editor once Phase 7 lands) is picked up.
            var packages = Queue
                .Select(entry => ExmodFolder.Read(_libraryRepository.GetFolderPath(entry.FolderName)))
                .ToList();

            var result = await _rebuildService.RebuildAsync(
                packages, _dataFolder, _settingsService.Current.UnrealPakExePath!, _outputPakPath);

            StatusMessage = $"Built '{result.OutputPakPath}' — {result.PackedFileCount} files packed, "
                + $"{result.MergedFileCount} data table(s) merged.";
            foreach (var warning in result.Warnings)
            {
                Warnings.Add(warning);
            }
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else in this app: a missing base data file, a
            // UnrealPak failure, or a malformed queued mod should show a status message, not
            // crash the app.
            StatusMessage = $"Rebuild failed: {ex.Message}";
        }
        finally
        {
            IsRebuilding = false;
        }
    }
}
