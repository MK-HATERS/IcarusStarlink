using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Deleting mods from the Library: the selection, one mod, or all, with a download's other parts taken out together.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>
    /// The context-menu's own Delete — operates on the WHOLE bulk selection, not just the row that
    /// happened to be right-clicked: code-behind's EnsureBulkSelected already folded the
    /// right-clicked row into BulkSelectedItems (either as the sole entry, or added to whatever
    /// Ctrl/Shift-selection already existed) before this menu ever opened, so BulkSelectedItems is
    /// always the right source of truth here — falling back to the passed-in item alone only
    /// covers a call site that isn't the context menu at all.
    /// </summary>
    [RelayCommand]
    private void DeleteItem(LibraryItemViewModel? item)
    {
        var items = BulkSelectedItems.Count > 0 ? BulkSelectedItems.ToList() : item is not null ? [item] : [];
        DeleteMods(items);
    }

    /// <summary>
    /// Deletes exactly the given mod, ignoring the list's click selection — for the mod detail
    /// window, which can show a different mod (Prev/Next) than the one selected in the list. It
    /// used DeleteItem, which deletes the list's selection whenever there is one.
    /// </summary>
    [RelayCommand]
    private void DeleteOneItem(LibraryItemViewModel? item) => DeleteMods(item is null ? [] : [item]);

    /// <summary>Deletes every mod in the Library, regardless of any current selection — its own menu item since this is a much larger blast radius than the ordinary per-selection Delete above; DeleteMods' own count-naming confirmation still gates it.</summary>
    [RelayCommand]
    private void DeleteAll() => DeleteMods([.. AllLibraryItems()]);

    private LibraryModRemover? _modRemover;

    private LibraryModRemover ModRemover => _modRemover ??= new LibraryModRemover(_repository, _prebuiltPakSourceStore, _pakPreviewCacheDirectory, FixService, _thumbnailCacheDirectory);

    private void DeleteMods(IReadOnlyList<LibraryItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        // A package (one Nexus download that landed as a Lua mod plus asset paks) is deleted whole:
        // the selection is widened to every sibling part, and the UE4SS parts come out too.
        // Half a WirelessPower is a broken WirelessPower, and the user has no way to know which
        // three rows belong together otherwise.
        var selectedFolders = new HashSet<string>(items.Select(i => i.FolderName), StringComparer.OrdinalIgnoreCase);
        var packages = items
            .Select(PackageOf)
            .OfType<ModPackage>()
            .Distinct()
            .ToList();
        var siblingItems = AllLibraryItems()
            .Where(i => !selectedFolders.Contains(i.FolderName) && packages.Any(p => p.LibraryFolderNames.Contains(i.FolderName, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var ue4ssParts = packages.SelectMany(p => p.Ue4ssFolderNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (siblingItems.Count > 0)
        {
            items = [.. items, .. siblingItems];
        }

        // A UE4SS half linked only by name (no shared Nexus download: a local, own-build or
        // Database import) is a linked half too, so the same question asks which halves go. It
        // used to get a plain Yes/No and its Lua half stayed in the game.
        var nameLinkedParts = items
            .Select(i => i.LinkedPartner)
            .OfType<Ue4ssModRowViewModel>()
            .Where(r => !r.IsBuiltIn && !Ue4ssModStart.IsFrameworkFolder(r.Name))
            .Select(r => r.Name)
            .Where(n => !ue4ssParts.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ue4ssParts = [.. ue4ssParts, .. nameLinkedParts];
        var downloadParts = ue4ssParts.Except(nameLinkedParts, StringComparer.OrdinalIgnoreCase).ToList();

        // Not while a check or fix pass rewrites mods, one of them is being converted again, or its
        // editor is open (the fix window's guards): asked to delete, it would race the pass, and the
        // editor's next Save would bring the deleted mod back half-made.
        if (items.Select(ModChangeBlockedReason).FirstOrDefault(r => r is not null) is { } blocked)
        {
            ReportProblem(blocked);
            return;
        }

        // Deleting removes each mod's real folder from disk and can't be undone from here — and a
        // right-click menu is far easier to hit by accident than a dedicated button, so this always
        // asks first, matching how every other irreversible action in this app behaves. Names the
        // real count and the mods rather than a generic "these mods" so a fat-fingered bulk
        // selection is obvious before it's too late to back out.
        string message;
        var whole = packages.Count == 1 && nameLinkedParts.Count == 0 && items.Count + ue4ssParts.Count == packages[0].PartCount;
        if (whole)
        {
            var package = packages[0];
            var partLines = items.Select(i => $"  • {i.Name} (Library)")
                .Concat(ue4ssParts.Select(n => $"  • {n} (UE4SS mod)"));
            message = $"Delete '{package.DisplayName}' and all {package.PartCount} of its parts?\n\n{string.Join("\n", partLines)}\n\nThe folders are removed from disk; an enabled UE4SS part is taken out of the game's Mods folder (backed up first). Run Merge & Install afterwards to drop the paks from the game. Each Library part is backed up first (Backups\\Mods).";
        }
        else
        {
            message = items.Count == 1
                ? $"Delete '{items[0].Name}' from your Library?\n\nIts folder is removed from disk (a backup is kept under Backups\\Mods). Run Merge & Install afterwards to drop it from the game."
                : $"Delete {items.Count} mods from your Library?\n\n{NameSome(items)}\n\nTheir folders are removed from disk (each is backed up first, under Backups\\Mods). Run Merge & Install afterwards to drop them from the game.";
            if (downloadParts.Count > 0)
            {
                message += $"\n\nThese came with UE4SS mods from the same download: {string.Join(", ", downloadParts)}.";
            }

            if (nameLinkedParts.Count > 0)
            {
                message += items.Count == 1 && nameLinkedParts.Count == 1
                    ? $"\n\nIt's linked to the UE4SS mod {nameLinkedParts[0]}."
                    : $"\n\nLinked UE4SS mods: {string.Join(", ", nameLinkedParts)}.";
            }
        }


        // A UE4SS part that is on in the game can't come out of its Mods folder while the game runs.
        // Asked anyway, the Library parts went after the Yes and the Lua half stayed in the game:
        // half a mod left behind. Refused before the question (the UE4SS panel's own Delete of a
        // linked half lands here too). Needed since in-game halves are linked.
        // Checked again after the Yes, before anything goes: the question gives the game time to
        // start (as Merge & Install's ConfirmWithGameClosed does).
        bool RefusedWhileGameRuns()
        {
            if (ue4ssParts.Count > 0 && Ue4ssPartsInGame(ue4ssParts) is { Count: > 0 } inGameParts && IsGameRunningCheck())
            {
                ReportProblem($"Icarus is running, and the UE4SS mod {string.Join(", ", inGameParts)} is on in the game. Close the game, then delete. Nothing was deleted.");
                Ue4ssStatusMessage = StatusMessage;
                return true;
            }

            return false;
        }

        if (RefusedWhileGameRuns())
        {
            return;
        }

        // A download with both halves: both go by default, or just one (the data half, keeping the
        // Lua mod, or the other way round). The half kept is an ordinary row afterwards.
        var keptHalf = "";
        if (ue4ssParts.Count > 0)
        {
            var choice = _dialogService.Choose(
                message + "\n\nDelete both halves, or only one of them? The half you keep stays in your Library on its own.",
                "Delete mod(s)", ThemedConfirmSeverity.Warning, "Delete both", "Only the data mod", "Only the UE4SS mod");
            switch (choice)
            {
                case null:
                    return;
                case 1:
                    keptHalf = $" Kept the UE4SS mod: {string.Join(", ", ue4ssParts)}.";
                    ue4ssParts = [];
                    break;
                case 2:
                    keptHalf = $" Kept the data mod: {string.Join(", ", items.Select(i => i.Name))}.";
                    items = [];
                    break;
            }
        }
        else if (!_dialogService.Confirm(message, "Delete mod(s)", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        if (RefusedWhileGameRuns())
        {
            return;
        }

        var deletedCount = 0;
        var ue4ssDeleted = 0;
        var undoable = new List<UndoableDelete>();
        var ue4ssDeletedNames = new List<string>();
        var failures = new List<string>();
        var anyLocked = false;
        foreach (var ue4ssPart in ue4ssParts)
        {
            var failure = DeleteUe4ssModCore(ue4ssPart, out var locked);
            anyLocked |= locked;
            if (failure is null)
            {
                ue4ssDeleted++;
                ue4ssDeletedNames.Add(ue4ssPart);
                _activityLog.Log($"Deleted UE4SS mod '{ue4ssPart}' (part of the same download).", ActivityEntryKind.Info);
            }
            else
            {
                failures.Add($"{ue4ssPart}: {failure}");
            }
        }

        foreach (var item in items)
        {
            var name = item.Name;
            // Saved now, not dropped: the backup below keeps the notes typed a moment ago, and a
            // timer left pending can't fire after the delete and write them into a different mod
            // that reuses the freed folder name.
            item.FlushPendingSave();
            var wasTicked = item.IsEnabled;
            try
            {
                // Backed up first, so a delete (an easy right-click to hit by accident) isn't
                // permanent — a mod that can't be backed up isn't deleted. Its remembered fix
                // choices, saved source pak and preview cache go with it (LibraryModRemover), and
                // are kept with the backup for the notice's Undo.
                undoable.Add(new UndoableDelete(ModRemover.Delete(item.FolderName), name, wasTicked));
                deletedCount++;
                if (SelectedItem == item)
                {
                    SelectedItem = null;
                }

                _activityLog.Log($"Deleted '{name}'.", ActivityEntryKind.Info);
            }
            catch (Exception ex)
            {
                // Same UI boundary as ImportPaths: deleting a folder that's locked by another
                // process (Explorer preview, an antivirus scan, ...) throws IOException/
                // UnauthorizedAccessException — one locked mod shouldn't abort the rest of a batch.
                failures.Add($"{name}: {ex.Message}");
                anyLocked |= ex is System.IO.IOException or UnauthorizedAccessException;
            }
        }

        ClearBulkSelection();
        var nextStep = deletedCount > 0 ? " Run Merge & Install to remove it from the game." : "";
        if (failures.Count > 0)
        {
            // Every part counts, the UE4SS halves too: a download whose Lua half went and whose
            // locked pak stayed used to read "Deleted 0 of 2". The next step only for a locked file:
            // closing something doesn't help a mod that is no longer there.
            var lockedNextStep = anyLocked ? " Close whatever is using its files, then try again." : "";
            ReportProblem($"Deleted {deletedCount + ue4ssDeleted} of {items.Count + ue4ssParts.Count} — {string.Join("; ", failures).TrimEnd('.')}.{lockedNextStep}{nextStep}");
        }
        else
        {
            StatusMessage = items.Count switch
            {
                0 => $"Deleted the UE4SS mod {string.Join(", ", ue4ssParts)}.{keptHalf}",
                _ when deletedCount == 1 && ue4ssParts.Count == 0 => $"Deleted '{items[0].Name}'.{nextStep}{keptHalf}",
                _ when packages.Count == 1 && nameLinkedParts.Count == 0 && ue4ssParts.Count > 0 => $"Deleted '{packages[0].DisplayName}' ({deletedCount + ue4ssDeleted} parts).{nextStep}",
                _ when ue4ssParts.Count > 0 => $"Deleted {deletedCount} mod(s) and the UE4SS mod {string.Join(", ", ue4ssParts)}.{nextStep}",
                _ => $"Deleted {deletedCount} mod(s).{nextStep}{keptHalf}",
            };
        }

        // Its own handler reloads both panels (the UE4SS parts too): no Reload of its own here.
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        OfferUndo(undoable, ue4ssDeletedNames);
    }

    /// <summary>The delete notice shows Undo: the last delete can still be put back.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoDeleteCommand))]
    private bool _canUndoDelete;

    /// <summary>One mod the last delete took: what Undo puts back, the name the notice uses, and whether it was ticked for Merge &amp; Install.</summary>
    private sealed record UndoableDelete(DeletedLibraryMod Mod, string Name, bool WasTicked);

    /// <summary>The last delete's mods (the Library halves; a UE4SS half that went with them isn't kept here).</summary>
    private List<UndoableDelete> _undoableDeletes = [];

    /// <summary>The UE4SS halves the last delete took too: Undo says it doesn't bring them back.</summary>
    private List<string> _undoSkippedUe4ss = [];

    /// <summary>Set after the delete's own notice is up, so only that notice offers Undo.</summary>
    private void OfferUndo(List<UndoableDelete> deleted, List<string> ue4ssDeleted)
    {
        _undoableDeletes = deleted;
        _undoSkippedUe4ss = ue4ssDeleted;
        CanUndoDelete = deleted.Count > 0;
    }

    /// <summary>The notice changed or closed: its Undo goes with it.</summary>
    private void WithdrawUndo()
    {
        _undoableDeletes = [];
        _undoSkippedUe4ss = [];
        CanUndoDelete = false;
    }

    /// <summary>
    /// Undo on the delete notice: each mod comes back from the backup its delete took, with its
    /// notes, Nexus link, source, pin and rename, its saved source pak and its fix choices
    /// (LibraryModRemover.Undo). One whose folder name a newer mod took comes back under a free
    /// name. The copy runs off the UI thread (a pak mod can be large). A mod that was ticked for
    /// Merge &amp; Install is ticked again.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoDelete))]
    private async Task UndoDeleteAsync()
    {
        var toUndo = _undoableDeletes;
        var skippedUe4ss = _undoSkippedUe4ss;
        WithdrawUndo();
        if (toUndo.Count == 0)
        {
            return;
        }

        var remover = ModRemover;
        var (restored, failures) = await Task.Run(() =>
        {
            var back = new List<(UndoableDelete Deleted, string FolderName)>();
            var failed = new List<string>();
            foreach (var deleted in toUndo)
            {
                try
                {
                    if (remover.Undo(deleted.Mod) is { } folderName)
                    {
                        back.Add((deleted, folderName));
                    }
                    else
                    {
                        failed.Add($"{deleted.Name}: its backup is gone");
                    }
                }
                catch (Exception ex)
                {
                    // Same UI boundary as Delete: one mod that can't be copied back doesn't stop the rest.
                    failed.Add($"{deleted.Name}: {ex.Message.TrimEnd('.')}");
                }
            }

            return (back, failed);
        });

        // Its own handler reloads the rows (see DeleteMods).
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        var ticked = 0;
        foreach (var (deleted, folderName) in restored)
        {
            _activityLog.Log($"Brought back '{deleted.Name}' after a delete.", ActivityEntryKind.Info);
            if (deleted.WasTicked && AllLibraryItems().FirstOrDefault(i => string.Equals(i.FolderName, folderName, StringComparison.OrdinalIgnoreCase)) is { } item)
            {
                // Raises EnabledToggled, which puts it in the Merge & Install queue.
                item.IsEnabled = true;
                ticked++;
            }
        }

        var movedNote = restored
            .Where(r => !string.Equals(r.FolderName, r.Deleted.Mod.FolderName, StringComparison.OrdinalIgnoreCase))
            .Select(r => $" A newer mod uses the folder name {r.Deleted.Mod.FolderName}, so '{r.Deleted.Name}' is in {r.FolderName}.")
            .FirstOrDefault() ?? "";
        var tickedNote = ticked switch
        {
            0 => "",
            1 when restored.Count == 1 => " It's ticked for Merge & Install again.",
            1 => " One of them is ticked for Merge & Install again.",
            _ => $" {ticked} of them are ticked for Merge & Install again.",
        };
        var ue4ssNote = skippedUe4ss.Count > 0 ? $" Undo doesn't bring back the UE4SS mod {string.Join(", ", skippedUe4ss)}: install it again to get it back." : "";
        var done = restored.Count == 1 ? $"Brought back '{restored[0].Deleted.Name}'." : $"Brought back {restored.Count} mods.";
        if (failures.Count > 0)
        {
            ReportProblem($"Brought back {restored.Count} of {toUndo.Count} — {string.Join("; ", failures)}.{movedNote}{tickedNote}");
        }
        else
        {
            StatusMessage = $"{done}{movedNote}{tickedNote}{ue4ssNote}";
        }
    }

    /// <summary>The several-mods question names them, so a selection that grew past what the list shows is caught: five names, then "and N more".</summary>
    private static string NameSome(IReadOnlyList<LibraryItemViewModel> items)
    {
        const int Shown = 5;
        var lines = string.Join("\n", items.Take(Shown).Select(i => $"  • {i.Name}"));
        return items.Count > Shown ? $"{lines}\n  and {items.Count - Shown} more" : lines;
    }

    /// <summary>Which of these UE4SS mods are in the game's Mods folder (none without a game path, or when it can't be read).</summary>
    private List<string> Ue4ssPartsInGame(IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            return [];
        }

        try
        {
            var inGame = _ue4ssModRepository.ListInstalledInGame(Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath));
            return [.. names.Where(n => inGame.Contains(n, StringComparer.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
