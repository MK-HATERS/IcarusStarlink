using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Migration;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel
{
    // --- Migrate from classic IMM ---

    [ObservableProperty]
    private bool _isMigratingFromImm;

    /// <summary>Computed bool rather than an inverting converter — the same pattern CanCheckForAppUpdates already uses, and the one this codebase settled on after repeated trouble with converter parameters.</summary>
    public bool CanMigrateFromImm => !IsMigratingFromImm;

    partial void OnIsMigratingFromImmChanged(bool value) => OnPropertyChanged(nameof(CanMigrateFromImm));

    [ObservableProperty]
    private string? _migrationStatusMessage;

    /// <summary>Optional — left empty, the classic IMM folder is worked out from wherever the picked mod list lives. Set it when your install isn't laid out that way (or just to be explicit).</summary>
    [ObservableProperty]
    private string? _immFolderPath;

    [RelayCommand]
    private void BrowseImmFolder()
    {
        if (_dialogService.PickFolder(ImmFolderPickerTitle) is not { } folder)
        {
            return;
        }

        ImmFolderPath = folder;
        MigrationStatusMessage = ImmInstallPaths.LooksLikeInstallRoot(folder)
            ? "Classic IMM folder set — now pick its mod list."
            : $"Careful: that folder has no Extracted_Mods + {ImmExtractedMods.FileName} in it, so it may not be a classic IMM install.";
    }

    private const string ImmFolderPickerTitle = "Select your classic IMM folder (the one containing Extracted_Mods)";

    public ObservableCollection<string> MigrationResults { get; } = [];

    public bool HasMigrationResults => MigrationResults.Count > 0;

    /// <summary>
    /// One-click migration: pick a classic IMM merge list, and every mod it names is copied out of
    /// IMM's own Extracted_Mods folder into this Library, linked to its Database or Nexus source
    /// where that can be determined, and finally queued in Merge &amp; Install in the list's own
    /// order — so the user's existing merge setup comes across whole instead of being rebuilt by
    /// hand.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportImmModListAsync()
    {
        if (_dialogService.PickFile(
                "Pick your classic IMM mod list (LastMergedMods.txt or IMM_Merged_Mod.txt)",
                "Mod list (*.txt, *.lst)|*.txt;*.lst|All files (*.*)|*.*") is not { } listPath)
        {
            return;
        }

        // An explicitly-chosen folder always wins — nobody's IMM install has to live where anyone
        // else's does, and auto-detection only works when the list happens to sit inside it.
        var installRoot = !string.IsNullOrWhiteSpace(ImmFolderPath) && ImmInstallPaths.LooksLikeInstallRoot(ImmFolderPath)
            ? ImmFolderPath
            : ImmInstallPaths.FindInstallRoot(listPath);

        if (installRoot is null)
        {
            // A list taken from the game's own Paks\mods folder has no IMM install anywhere near
            // it, so the mods themselves can't be found without being told where they live.
            MigrationStatusMessage = "That list isn't inside a classic IMM folder — pick your classic IMM folder so the mods can be found.";
            if (_dialogService.PickFolder(ImmFolderPickerTitle) is not { } pickedRoot)
            {
                MigrationStatusMessage = null;
                return;
            }

            if (!ImmInstallPaths.LooksLikeInstallRoot(pickedRoot))
            {
                MigrationStatusMessage = $"'{pickedRoot}' doesn't look like a classic IMM folder — it needs an Extracted_Mods folder and an {ImmExtractedMods.FileName} file.";
                return;
            }

            installRoot = pickedRoot;
        }

        // Shown back in the box so it's visible what was used, and reusable next time.
        ImmFolderPath = installRoot;

        IsMigratingFromImm = true;
        MigrationResults.Clear();
        OnPropertyChanged(nameof(HasMigrationResults));

        try
        {
            var progress = new Progress<string>(message => MigrationStatusMessage = message);
            var result = await _immMigrationService.MigrateAsync(listPath, installRoot, progress);

            foreach (var mod in result.Mods)
            {
                MigrationResults.Add(mod.Display);
            }

            OnPropertyChanged(nameof(HasMigrationResults));

            // The mods are here; now bring the merge list itself across, which is the half that
            // actually saves the user rebuilding their setup. Resolved directly rather than
            // announced by message: page ViewModels are constructed lazily on first navigation,
            // so a user who migrates before ever opening Merge & Install would have no instance
            // listening, and the queue would silently stay empty (found live).
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            var mergeInstall = _mergeInstallViewModel();
            mergeInstall.LoadModListFromFile(listPath);

            var linked = new List<string>();
            if (result.LinkedToDatabaseCount > 0)
            {
                linked.Add($"{result.LinkedToDatabaseCount} linked to the mod database");
            }

            if (result.LinkedToNexusCount > 0)
            {
                linked.Add($"{result.LinkedToNexusCount} linked to Nexus");
            }

            // "Not found" and "found but wouldn't import" are genuinely different problems with
            // different fixes (get the mod vs. fix/replace a broken one), so they're never merged
            // into one count — the failed ones also carry their real reason in the list below.
            var problems = new List<string>();
            if (result.MissingCount > 0)
            {
                problems.Add($"{result.MissingCount} not found in classic IMM's mods folder");
            }

            if (result.FailedCount > 0)
            {
                problems.Add($"{result.FailedCount} couldn't be imported");
            }

            MigrationStatusMessage =
                $"Brought over {result.ImportedCount} mod(s)"
                + (result.AlreadyPresentCount > 0 ? $", {result.AlreadyPresentCount} already here" : "")
                + (linked.Count > 0 ? $" ({string.Join(", ", linked)})" : "")
                + (problems.Count > 0 ? $" — {string.Join(", ", problems)}; see the list below." : ".")
                + " Your merge list is now queued in Merge & Install."
                + (result.FailedCatalogSources.Count > 0
                    ? $" ({string.Join(" and ", result.FailedCatalogSources)} unavailable — some mods may have missed a database link; try migrating again.)"
                    : "");
        }
        catch (Exception ex)
        {
            MigrationStatusMessage = $"Migration failed: {ex.Message}";
        }
        finally
        {
            IsMigratingFromImm = false;
        }
    }
}
