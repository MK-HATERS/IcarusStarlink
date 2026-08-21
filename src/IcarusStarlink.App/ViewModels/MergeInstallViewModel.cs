using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Rebuild;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 6 (see the plan's "Update (2026-08-21)" section): merge queue + Rebuild (6.1) + Install
/// (6.2). Rebuild only ever writes to a staged pak under IcarusStarlink's own folder; Install is
/// the one action in this whole app that writes into the real game's Content\Paks\mods —
/// deliberately its own separate, explicit button rather than folded into Rebuild, so a click
/// there is never accidental.
/// </summary>
public sealed partial class MergeInstallViewModel : ObservableObject
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IRebuildService _rebuildService;
    private readonly IInstallService _installService;
    private readonly ISettingsService _settingsService;
    private readonly string _dataFolder;
    private readonly string _outputPakPath;
    private readonly string _backupDirectory;
    private string? _lastManifestPath;

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

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string? _installStatusMessage;

    public ObservableCollection<string> Warnings { get; } = [];

    public MergeInstallViewModel(
        ILibraryRepository libraryRepository, IRebuildService rebuildService, IInstallService installService, ISettingsService settingsService,
        string dataFolder, string outputPakPath, string backupDirectory)
    {
        _libraryRepository = libraryRepository;
        _rebuildService = rebuildService;
        _installService = installService;
        _settingsService = settingsService;
        _dataFolder = dataFolder;
        _outputPakPath = outputPakPath;
        _backupDirectory = backupDirectory;

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
            _lastManifestPath = result.ManifestPath;

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

    /// <summary>
    /// The one command in this whole app that writes into the real game's Content\Paks\mods.
    /// Also serves as the spec's own "Copy built pack to game" retry action — clicking it again
    /// after a locked-file failure just re-copies the same already-staged pak, no rebuild needed.
    /// </summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        if (!File.Exists(_outputPakPath))
        {
            InstallStatusMessage = "Nothing staged yet — click Rebuild first.";
            return;
        }

        IsInstalling = true;
        InstallStatusMessage = "Installing…";

        try
        {
            var result = await _installService.InstallAsync(
                _outputPakPath, _lastManifestPath, _settingsService.Current.IcarusContentPath!, _backupDirectory);

            // Replace, not accumulate: ImportPak would otherwise derive a fresh "_2"/"_3"-suffixed
            // folder name every time (its own collision-avoidance rule), leaving one stale Library
            // entry behind per install instead of one entry that stays current.
            var installedFolderName = Path.GetFileNameWithoutExtension(_outputPakPath);
            var existing = _libraryRepository.GetAll().FirstOrDefault(e => e.FolderName == installedFolderName);
            if (existing is not null)
            {
                _libraryRepository.Delete(existing.FolderName);
            }
            _libraryRepository.ImportPak(_outputPakPath);
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

            InstallStatusMessage = result.BackupPakPath is not null
                ? $"Installed to '{result.InstalledPakPath}'. Backed up the previous pak to '{result.BackupPakPath}'."
                : $"Installed to '{result.InstalledPakPath}'.";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a locked target file (the game running), a
            // missing/wrong Content path, or a permissions issue should show a status message,
            // not crash the app. Retry is just clicking Install again once the cause is cleared.
            InstallStatusMessage = $"Install failed: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
        }
    }
}
