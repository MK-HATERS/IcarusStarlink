using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;

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

    private LibraryModRemover ModRemover => _modRemover ??= new LibraryModRemover(_repository, _prebuiltPakSourceStore, _pakPreviewCacheDirectory, FixService);

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

        // Not while a check or fix pass rewrites mods, one of them is being converted again, or its
        // editor is open (the fix window's guards): asked to delete, it would race the pass, and the
        // editor's next Save would bring the deleted mod back half-made.
        if (items.Select(ModChangeBlockedReason).FirstOrDefault(r => r is not null) is { } blocked)
        {
            StatusMessage = blocked;
            return;
        }

        // Deleting removes each mod's real folder from disk and can't be undone from here — and a
        // right-click menu is far easier to hit by accident than a dedicated button, so this always
        // asks first, matching how every other irreversible action in this app behaves. Names the
        // real count rather than a generic "these mods" so a fat-fingered bulk selection is obvious
        // before it's too late to back out.
        string message;
        if (packages.Count == 1 && items.Count + ue4ssParts.Count == packages[0].PartCount)
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
                : $"Delete {items.Count} mods from your Library?\n\nTheir folders are removed from disk (each is backed up first, under Backups\\Mods). Run Merge & Install afterwards to drop them from the game.";
            if (ue4ssParts.Count > 0)
            {
                message += $"\n\nThese came with UE4SS mods from the same download, which are removed too: {string.Join(", ", ue4ssParts)}.";
            }
        }

        // A UE4SS part that is on in the game can't come out of its Mods folder while the game runs.
        // Asked anyway, the Library parts went after the Yes and the Lua half stayed in the game:
        // half a mod left behind. Refused before the question (the UE4SS panel's own Delete of a
        // linked half lands here too). Needed again since in-game halves are linked (2026-10-28).
        if (ue4ssParts.Count > 0 && Ue4ssPartsInGame(ue4ssParts) is { Count: > 0 } inGameParts && IsGameRunningCheck())
        {
            StatusMessage = $"Icarus is running, and {string.Join(", ", inGameParts)} from the same download is on in the game. Close the game, then delete.";
            Ue4ssStatusMessage = StatusMessage;
            return;
        }

        var confirm = _dialogService.Confirm(message, "Delete mod(s)", ThemedConfirmSeverity.Warning);
        if (!(confirm))
        {
            return;
        }

        var deletedCount = 0;
        var failures = new List<string>();
        foreach (var ue4ssPart in ue4ssParts)
        {
            var failure = DeleteUe4ssModCore(ue4ssPart);
            if (failure is null)
            {
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
            try
            {
                // Backed up first, so a delete (an easy right-click to hit by accident) isn't
                // permanent — a mod that can't be backed up isn't deleted. Its remembered fix
                // choices, saved source pak and preview cache go with it (LibraryModRemover).
                ModRemover.Delete(item.FolderName);
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
            }
        }

        ClearBulkSelection();
        var nextStep = deletedCount > 0 ? " Run Merge & Install to remove it from the game." : "";
        StatusMessage = failures.Count switch
        {
            0 when deletedCount == 1 && ue4ssParts.Count == 0 => $"Deleted '{items[0].Name}'.{nextStep}",
            0 when packages.Count == 1 && ue4ssParts.Count > 0 => $"Deleted '{packages[0].DisplayName}' ({deletedCount + ue4ssParts.Count} parts).{nextStep}",
            0 => $"Deleted {deletedCount} mod(s).{nextStep}",
            _ => $"Deleted {deletedCount} of {items.Count} — {string.Join("; ", failures)}",
        };
        // Its own handler reloads both panels (the UE4SS parts too): no Reload of its own here.
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
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
            var inGame = _ue4ssModRepository.ListInstalledInGame(IcarusStarlink.Core.Ue4ss.Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath));
            return [.. names.Where(n => inGame.Contains(n, StringComparer.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
