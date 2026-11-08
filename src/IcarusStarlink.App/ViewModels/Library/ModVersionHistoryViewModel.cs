using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One mod's version history: every backup Starlink kept of it (before an update, a fix, a
/// reinstall, or made by hand), newest first, with Put this version back and Compare with now.
/// Transient, built by LibraryViewModel.ShowVersionHistory; the page does the real work through
/// the callbacks, so the window holds no file code.
/// </summary>
public sealed partial class ModVersionHistoryViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<ModBackupInfo>> _load;
    private readonly Func<ModBackupInfo, Task<bool>> _restore;
    private readonly Func<ModBackupInfo, Task> _compare;

    public ModVersionHistoryViewModel(
        string modName, Func<IReadOnlyList<ModBackupInfo>> load, Func<ModBackupInfo, Task<bool>> restore, Func<ModBackupInfo, Task> compare)
    {
        ModName = modName;
        _load = load;
        _restore = restore;
        _compare = compare;
        Reload();
    }

    public string ModName { get; }

    public string Title => $"Versions of {ModName}";

    public ObservableCollection<ModVersionEntryViewModel> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    public bool HasNoEntries => Entries.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(CompareCommand))]
    private ModVersionEntryViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(CompareCommand))]
    private bool _isBusy;

    /// <summary>Reads the backups again (after a restore added one).</summary>
    public void Reload()
    {
        var selectedPath = SelectedEntry?.Info.Path;
        Entries.Clear();
        foreach (var info in _load())
        {
            Entries.Add(new ModVersionEntryViewModel(info));
        }

        SelectedEntry = Entries.FirstOrDefault(e => string.Equals(e.Info.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? Entries.FirstOrDefault();
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasNoEntries));
    }

    private bool CanAct() => SelectedEntry is not null && !IsBusy;

    /// <summary>Puts the picked version back (the page asks first and backs up the current copy).</summary>
    [RelayCommand(CanExecute = nameof(CanAct), FlowExceptionsToTaskScheduler = true)]
    private async Task RestoreAsync()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (await _restore(entry.Info))
            {
                Reload();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows what differs between the picked version and the copy in the Library now. Changes nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanAct), FlowExceptionsToTaskScheduler = true)]
    private async Task CompareAsync()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _compare(entry.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One row of the version history.</summary>
public sealed class ModVersionEntryViewModel(ModBackupInfo info)
{
    public ModBackupInfo Info { get; } = info;

    /// <summary>When it was kept, in the player's own time ("8 Nov 2026, 16:15"); "Unknown time" for a folder without a time in its name.</summary>
    public string WhenText => Info.TakenAtUtc is { } when
        ? when.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture)
        : "Unknown time";

    /// <summary>Why it was kept; a backup from before Starlink wrote reasons says just "Backup".</summary>
    public string ReasonText => Info.Reason ?? "Backup";

    /// <summary>"v1.2", or empty when the mod had no version.</summary>
    public string VersionText => VersionLabel.For(Info.Version);

    /// <summary>"Nexus file 1234", or empty when it didn't come from a known Nexus file.</summary>
    public string FileText => Info.NexusFileId is { } id ? $"Nexus file {id}" : "";

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{WhenText}, {ReasonText} {VersionText}".Trim();
}
