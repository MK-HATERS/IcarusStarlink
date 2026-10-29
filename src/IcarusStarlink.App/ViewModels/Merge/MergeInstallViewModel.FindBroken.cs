using System.IO;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// What Find the broken mod (FindBrokenModViewModel, BrokenModFinderSession) uses of this page:
/// a build and install of only some of the queued mods, the same build and install the Install
/// button runs, with the queue itself left as it is. That's the safety of the whole search: the
/// player's list never changes, so putting the mods back is one normal install of it.
/// </summary>
public sealed partial class MergeInstallViewModel
{
    /// <summary>While a finder round builds: only these queued mods go in (TakeSnapshot). Null otherwise.</summary>
    private IReadOnlySet<string>? _finderOnly;

    /// <summary>While the finder puts the whole queue back in the game.</summary>
    private bool _finderRestoring;

    /// <summary>
    /// Whether this finder install backs up the merged pak in the game first (InstallService's
    /// backUpWhatsThere). True for every normal install; false only while a finder round after the
    /// first (or putting the mods back) replaces a round's own pak, so the player's real install
    /// (backed up by the first round) stays the newest backup for Restore previous install.
    /// </summary>
    private bool _finderBacksUp = true;

    /// <summary>
    /// A finder round (or putting the mods back) doesn't ask "Install to Icarus?" again: the
    /// player asked for it in the finder's window, which says what goes in. It still asks when the
    /// install would also remove other files from the mods folder, as a normal install does.
    /// </summary>
    private bool FinderSkipsInstallQuestion(int otherFilesToClear, int oldArcFissionCopies) =>
        (_finderOnly is not null || _finderRestoring) && otherFilesToClear == 0 && oldArcFissionCopies == 0;

    /// <summary>More ▾ › Find the broken mod: opens the finder (FindBrokenModLauncher listens).</summary>
    [RelayCommand]
    private void FindBrokenMod() => WeakReferenceMessenger.Default.Send(new OpenFindBrokenModMessage());

    /// <summary>The queued mods a build would use (no greyed missing rows, no Starlink-built packs), in queue order.</summary>
    internal IReadOnlyList<LibraryEntry> FinderQueue() => [.. Queue.Where(IsBuildable)];

    /// <summary>The queued mods with their EXMODs read (a prebuilt pak has none), for the finder's suspects and links.</summary>
    internal async Task<IReadOnlyList<BrokenModCandidate>> ReadFinderCandidatesAsync()
    {
        var queue = FinderQueue();
        var loaded = await LoadQueuedPackagesAsync(queue);
        var packages = new Dictionary<string, ExmodPackage>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < loaded.Entries.Count; i++)
        {
            packages[loaded.Entries[i].FolderName] = loaded.Packages[i].Package;
        }

        return [.. queue.Select(e => new BrokenModCandidate(e.FolderName, e.Name, packages.GetValueOrDefault(e.FolderName)))];
    }

    /// <summary>The game tables the merge check reads (shared, cached).</summary>
    internal GameTableIndex FinderGameIndex() => GetOrCreateGameTableIndex();

    /// <summary>Why a finder install can't start now (the game runs, or this page is busy); null when it can.</summary>
    private string? FinderBlockedReason()
    {
        if (IsGameRunning())
        {
            return BrokenModFinderSession.GameRunningMessage;
        }

        if (IsRebuilding || IsInstalling)
        {
            return "Merge & Install is busy building or installing. Wait for it to finish, then try again.";
        }

        return null;
    }

    /// <summary>
    /// A finder round: builds and installs only these queued mods (in queue order, with the merge
    /// options as they are). With none of them, the merged pak is taken out of the game instead.
    /// Null when installed; otherwise why not (said in the finder's window). backUpWhatsThere:
    /// false for every round after the first (BrokenModSearchRecord.PlayerInstallBackedUp).
    /// </summary>
    internal async Task<string?> InstallForFinderAsync(IReadOnlyCollection<string> folderNames, bool backUpWhatsThere = true)
    {
        if (FinderBlockedReason() is { } blocked)
        {
            return blocked;
        }

        _finderOnly = folderNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _finderBacksUp = backUpWhatsThere;
        try
        {
            if (!FinderQueue().Any(e => _finderOnly.Contains(e.FolderName)) && !BuildGameplayOptionsFromUi().IsAnyActive)
            {
                return await RemoveMergeForFinderAsync();
            }

            return await BuildAndInstallForFinderAsync();
        }
        finally
        {
            _finderOnly = null;
            _finderBacksUp = true;
        }
    }

    /// <summary>
    /// The end of a search: every mod in <paramref name="putBack"/> that's still in the Library but
    /// not queued goes back in the queue, then the whole queue is built and installed, as the
    /// Install button does. Null when installed; otherwise why not. backUpWhatsThere false once a
    /// round has backed up the player's real install (the game then holds a round's pak).
    /// </summary>
    internal async Task<string?> InstallWholeQueueForFinderAsync(IReadOnlyCollection<string> putBack, bool backUpWhatsThere = true)
    {
        if (FinderBlockedReason() is { } blocked)
        {
            return blocked;
        }

        var inLibrary = _libraryRepository.GetAll().Select(e => e.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = putBack.Where(f => inLibrary.Contains(f) && !Queue.Any(q => !q.IsMissingFromLibrary && string.Equals(q.FolderName, f, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            AddToQueueByFolderNames(missing);
        }

        _finderRestoring = true;
        _finderBacksUp = backUpWhatsThere;
        try
        {
            if (!Queue.Any(IsBuildable) && !BuildGameplayOptionsFromUi().IsAnyActive)
            {
                // The player's own list is empty: nothing of theirs belongs in the game.
                return await RemoveMergeForFinderAsync();
            }

            return await BuildAndInstallForFinderAsync();
        }
        finally
        {
            _finderRestoring = false;
            _finderBacksUp = true;
        }
    }

    private async Task<string?> BuildAndInstallForFinderAsync()
    {
        if (!await RebuildAsync())
        {
            return StatusMessage ?? "The merge didn't build.";
        }

        return await InstallAsync() == InstallOutcome.Installed
            ? null
            : InstallStatusMessage ?? "The merge wasn't installed.";
    }

    /// <summary>A round with no data mods at all: Starlink's merged pak out of the game (backed up first when it's the search's first change).</summary>
    private async Task<string?> RemoveMergeForFinderAsync()
    {
        if (_settingsService.Current.IcarusContentPath is not { Length: > 0 } content)
        {
            return "Set the Icarus Content folder in Settings first.";
        }

        IsInstalling = true;
        try
        {
            await _installService.RemoveInstalledPakAsync(Path.GetFileName(_outputPakPath), content, _backupDirectory, backUpWhatsThere: _finderBacksUp);
            SetHasExistingInstall(false);
            await RefreshInstalledModNamesAsync();
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't take the merged mods out of the game: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
        }
    }
}
