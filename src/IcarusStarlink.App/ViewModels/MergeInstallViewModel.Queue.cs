using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The merge queue: adding, removing and reordering mods, the baseline list, Suggest order, and ticking again what the game has.</summary>
public sealed partial class MergeInstallViewModel
{
    /// <summary>
    /// The mods the installed merged pak was built from, ticked again in the order they were merged
    /// — for when the queue was lost (an older version that didn't save it, a profile switch) while
    /// the game still has them. Returns how many were ticked.
    /// </summary>
    public int RestoreQueueFromGame()
    {
        if (RefuseWhileRebuilding())
        {
            return 0;
        }

        var library = _libraryRepository.GetAll();
        var restored = new List<LibraryEntry>();
        var opaqueByPak = library.Where(e => e.IsOpaquePak && e.PakFileNames.Count > 0 && e.MergedPackModNames is null).ToList();
        foreach (var name in LastInstalledModNames)
        {
            var entry = library.FirstOrDefault(e => !e.IsOpaquePak && (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(e.FolderName, name, StringComparison.OrdinalIgnoreCase)))
                ?? opaqueByPak.FirstOrDefault(e => e.PakFileNames.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)));
            if (entry is not null && !restored.Contains(entry))
            {
                restored.Add(entry);
            }
        }

        if (restored.Count == 0)
        {
            return 0;
        }

        foreach (var entry in restored.Where(e => !Queue.Any(q => string.Equals(q.FolderName, e.FolderName, StringComparison.OrdinalIgnoreCase))))
        {
            Queue.Add(entry);
        }

        StatusMessage = $"Ticked the {restored.Count} mod(s) that are in the game, in the order they were merged.";
        _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        return restored.Count;
    }

    /// <summary>
    /// A pack Merge &amp; Install itself built (its Library entry came with an ISL-Merged.txt, or it
    /// has the merged pak's name). It can't go into the queue: it would fold the previous merge into
    /// the new one, and apply the merge options (Stacks x2…) a second time on top of the first.
    /// </summary>
    internal bool IsIslBuiltPack(LibraryEntry entry) =>
        entry.MergedPackModNames is not null
        || entry.MergedPackOptionDescriptions is not null
        || InstallManifestNames.AllMergedPakFileNames
            .Prepend(Path.GetFileName(_outputPakPath))
            .Any(n => string.Equals(entry.FolderName, Path.GetFileNameWithoutExtension(n), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reorders the queue's real EXMOD mods so the ones making fewer genuine edits (compared to
    /// current base game data) sit later — later wins conflicts by default, so a small, deliberate
    /// field change no longer loses to a big overhaul-style mod that happens to also touch the
    /// same field, often as a stale whole-item-copy artifact rather than a deliberate one. See
    /// MergeEngine.CountChangesDifferingFromBase's own doc comment for why raw field count alone
    /// isn't a safe enough signal on its own — a mod's declared field count can be inflated by
    /// unchanged copied fields an old-style extractor carried along.
    ///
    /// Opaque paks keep their own exact queue position — there's no per-field diff to rank them by
    /// without a real UnrealPak extraction — only the EXMOD entries around them get reordered into
    /// the slots they already occupied. This only ever suggests; nothing is applied without an
    /// explicit confirmation naming the exact new order.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task SuggestQueueOrderAsync()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        // A synchronous, no-I/O count (IsOpaquePak is a plain LibraryEntry field) rather than
        // LoadQueuedPackagesAsync's own real disk read — that happens exactly once, inside
        // ComputeSuggestedQueueOrderAsync below, not a second time redundantly just for this gate.
        if (Queue.Count(e => !e.IsOpaquePak && !e.IsMissingFromLibrary) < 2)
        {
            StatusMessage = "Nothing to reorder — need at least 2 real (non-prebuilt-pak) mods in the queue.";
            return;
        }

        var (newOrder, changed) = await ComputeSuggestedQueueOrderAsync();

        if (!changed)
        {
            StatusMessage = "The queue is already in a reasonable order — no change suggested.";
            return;
        }

        // The queue saves itself into the profile in use anyway (MarkWorkingStateChanged); with a
        // profile selected, the explicit "Apply this order? Yes" also makes a Save profile, so the
        // order before it is kept as that profile's backup.
        var saveNote = SelectedProfileName is { } selectedProfileName
            ? $" and save it to '{selectedProfileName}'"
            : "";
        var preview = string.Join("\n", newOrder.Select((e, i) => $"{i + 1}. {e.Name}"));
        var confirmed = Confirm(
            "Suggests putting mods that make fewer real edits (compared to current game data) later in the queue, " +
            "so a small, deliberate change doesn't lose to a bigger mod that happens to also touch the same field.\n\n" +
            $"New order:\n{preview}\n\nApply this order{saveNote}?",
            "Suggested order");
        if (!confirmed)
        {
            return;
        }

        Queue.Clear();
        foreach (var entry in newOrder)
        {
            Queue.Add(entry);
        }

        if (SelectedProfileName is { } name)
        {
            SaveProfile();
            StatusMessage = $"Applied the suggested order and saved it to '{name}'.";
        }
        else
        {
            StatusMessage = "Applied the suggested order.";
        }

        _activityLog.Log("Applied a suggested queue order.", ActivityEntryKind.Info);
    }

    /// <summary>
    /// SuggestQueueOrderAsync's own reordering algorithm, extracted so a test can exercise it
    /// directly — the rest of that method (the confirmation preview/apply/save) goes through
    /// a real blocking dialog (IDialogService) in the app.
    /// Internal, not private, purely for that seam. Reads the live Queue/package cache itself
    /// (same as the caller above did inline) rather than taking them as parameters, so a test calls
    /// this exactly the way SuggestQueueOrderAsync itself does.
    /// </summary>
    internal async Task<(List<LibraryEntry> NewOrder, bool Changed)> ComputeSuggestedQueueOrderAsync()
    {
        var loaded = await LoadQueuedPackagesAsync();
        var (entries, packages) = (loaded.Entries, loaded.Packages);
        var classifier = new DefaultSemanticClassifier();
        var currentQueue = Queue.ToList();

        return await Task.Run(() =>
        {
            var modChangesByFolder = entries.Zip(packages, (entry, package) =>
                    (entry.FolderName, Changes: ExmodFieldChangeMapper.ToFieldChanges(package.Package, classifier)))
                .ToDictionary(x => x.FolderName, x => x.Changes, StringComparer.OrdinalIgnoreCase);

            // Best-effort, matching FindQueueConflictsAsync's own fallback — no filtering (falls
            // back to raw field count) rather than blocking the suggestion on a locked/unreadable
            // base file.
            IReadOnlyDictionary<string, JsonObject>? baseTablesByFile = null;
            try
            {
                var requiredFiles = modChangesByFolder.Values.SelectMany(c => c).Select(c => c.CurrentFile).Distinct();
                baseTablesByFile = _rebuildService.ReadKeyedBaseTables(requiredFiles, _dataFolder, new MergeReport());
            }
            catch (Exception)
            {
                // Best-effort — see the comment above.
            }

            var realChangeCountByFolder = modChangesByFolder.ToDictionary(
                kv => kv.Key,
                kv => baseTablesByFile is not null
                    ? MergeEngine.CountChangesDifferingFromBase(kv.Value, baseTablesByFile)
                    : kv.Value.Count,
                StringComparer.OrdinalIgnoreCase);

            // Sort descending by real change count (most sweeping first/lowest priority, fewest
            // changes last/highest priority — so a targeted edit wins), then slot the sorted EXMOD
            // entries back into their own original positions, leaving any opaque pak exactly where
            // it was.
            var sortedExmodEntries = currentQueue
                .Where(e => realChangeCountByFolder.ContainsKey(e.FolderName))
                .OrderByDescending(e => realChangeCountByFolder[e.FolderName])
                .ToList();

            var nextSortedIndex = 0;
            var newOrder = currentQueue
                .Select(e => realChangeCountByFolder.ContainsKey(e.FolderName) ? sortedExmodEntries[nextSortedIndex++] : e)
                .ToList();

            var changed = !newOrder.Select(e => e.FolderName)
                .SequenceEqual(currentQueue.Select(e => e.FolderName), StringComparer.OrdinalIgnoreCase);
            return (newOrder, changed);
        });
    }

    /// <summary>
    /// Called from Library's own "Add to merge queue" action (Ctrl/Shift-click multi-select +
    /// right-click, or the toolbar button once 2+ rows are selected) — the only way mods reach this
    /// queue now that this page's own Library browsing pane is gone (redesign-round-2 pilot). Each
    /// folder name is looked up fresh via the repository rather than requiring Library to hold onto
    /// a LibraryEntry reference of its own.
    /// </summary>
    public void AddToQueueByFolderNames(IReadOnlyList<string> folderNames)
    {
        if (folderNames.Count == 0 || RefuseWhileRebuilding())
        {
            return;
        }

        // Mods already in the queue are no request at all (a tick that was already on): nothing to say.
        folderNames = [.. folderNames.Where(f => !Queue.Any(q => !q.IsMissingFromLibrary && string.Equals(q.FolderName, f, StringComparison.OrdinalIgnoreCase)))];
        if (folderNames.Count == 0)
        {
            return;
        }

        var byFolder = _libraryRepository.GetAll().ToDictionary(e => e.FolderName, StringComparer.OrdinalIgnoreCase);
        var addedCount = 0;
        string? lastRefusal = null;
        foreach (var folderName in folderNames)
        {
            if (byFolder.TryGetValue(folderName, out var entry))
            {
                if (AddEntryToQueue(entry))
                {
                    addedCount++;
                }
                else
                {
                    lastRefusal = StatusMessage;
                }
            }
        }

        // The reason a mod wasn't added stays readable (it used to be overwritten right here).
        StatusMessage = addedCount == folderNames.Count
            ? $"Added {addedCount} mod(s) to the merge queue."
            : addedCount == 0 && lastRefusal is not null
                ? lastRefusal
                : $"Added {addedCount} of {folderNames.Count} mod(s) to the merge queue.{(lastRefusal is null ? "" : $" {lastRefusal}")}";
        _activityLog.Log($"Added {addedCount} mod(s) to the merge queue from Library.", ActivityEntryKind.Info);
    }

    /// <summary>Whether this Library folder is in the merge queue — the Library's own "On" checkbox for a pak/EXMOD mod reads this.</summary>
    public bool IsQueued(string folderName) =>
        Queue.Any(q => string.Equals(q.FolderName, folderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Library's "On" checkbox unticked: the mod leaves the queue (the next Merge &amp; Install drops it from the game).</summary>
    public void RemoveFromQueueByFolderNames(IReadOnlyList<string> folderNames)
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        foreach (var entry in Queue.Where(q => folderNames.Contains(q.FolderName, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Queue.Remove(entry);
        }
    }

    /// <summary>
    /// Adds one Library mod to the end of the queue: a pack Merge &amp; Install built is refused (see
    /// IsIslBuiltPack), a mod already queued is left where it is, and a greyed missing row for it
    /// takes the real mod back in its place. Returns whether the entry actually got added, so a bulk
    /// caller can tally a real count; the reason it wasn't is in StatusMessage.
    /// </summary>
    private bool AddEntryToQueue(LibraryEntry entry)
    {
        // ISL's own merged pack (the Library keeps a copy of every build): queuing it folded the
        // previous merge into the new one and applied the merge options a second time on top.
        if (IsIslBuiltPack(entry))
        {
            StatusMessage = $"'{entry.Name}' is a pack Merge & Install built from your queue, so it can't be ticked itself: "
                + "tick the mods it was built from (merging it would put the old build into the new one and apply the merge options twice).";
            return false;
        }

        var existingIndex = Queue.ToList().FindIndex(q => string.Equals(q.FolderName, entry.FolderName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (Queue[existingIndex].IsMissingFromLibrary)
            {
                // The profile's missing mod is back: it takes its old place.
                Queue[existingIndex] = entry;
                return true;
            }

            StatusMessage = $"'{entry.Name}' is already in the queue.";
            return false;
        }

        Queue.Add(entry);
        return true;
    }

    [RelayCommand]
    private void RemoveFromQueue()
    {
        if (!RefuseWhileRebuilding() && SelectedQueueEntry is { } entry)
        {
            Queue.Remove(entry);
        }
    }

    /// <summary>
    /// Empties the list, which unticks every mod in the Library and is auto-saved into the
    /// profile: asked first, and the list is kept as a backup point first so Restore profile
    /// backup brings it back (the auto-save itself makes no backup).
    /// </summary>
    [RelayCommand]
    private void ClearQueue()
    {
        if (RefuseWhileRebuilding() || Queue.Count == 0)
        {
            return;
        }

        var profile = SelectedProfileName ?? "Default";
        if (!_dialogs.Confirm(
                $"Untick all {Queue.Count} mods and empty the list of profile '{profile}'?\n\nYour mods stay in the Library. Profile ▾ › Restore profile backup brings this list back.",
                "Clear the merge list", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        try
        {
            // Write the list as it is now first (an auto-save may still be pending), then save it
            // again so the backup made by that second save is this list.
            var current = CurrentWorkingProfile(profile);
            _profileStore.SaveWithoutBackup(current);
            _profileStore.Save(current);
            RefreshCanRestoreProfileBackup();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't back up the list first ({ex.Message}), so it wasn't cleared.";
            return;
        }

        Queue.Clear();
    }

    [RelayCommand]
    private void MoveQueueEntryUp()
    {
        if (RefuseWhileRebuilding() || SelectedQueueEntry is not { } entry)
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
        if (RefuseWhileRebuilding() || SelectedQueueEntry is not { } entry)
        {
            return;
        }

        var index = Queue.IndexOf(entry);
        if (index >= 0 && index < Queue.Count - 1)
        {
            Queue.Move(index, index + 1);
        }
    }

    /// <summary>Drag-and-drop reordering (MergeInstallView's own code-behind drives the actual drag
    /// gesture — WPF has no clean MVVM path for it) — same Queue.Move the up/down buttons already
    /// use, so it triggers the identical CollectionChanged-driven manual-pick invalidation above
    /// for free.</summary>
    public void ReorderQueueEntry(LibraryEntry entry, int targetIndex)
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        var currentIndex = Queue.IndexOf(entry);
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= Queue.Count || currentIndex == targetIndex)
        {
            return;
        }

        Queue.Move(currentIndex, targetIndex);
    }
}
