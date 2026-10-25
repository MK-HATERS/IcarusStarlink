using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The "On" tick's link to the merge queue, each row's Status, and the "not in the game yet" bars.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>True when an "On" tick differs from what the installed merged pak contains (Merge &amp; Install needed) or a UE4SS tick differs from disk (Apply needed).</summary>
    [ObservableProperty]
    private bool _hasPendingChanges;

    /// <summary>"2 changes not in the game yet: 1 UE4SS change and 1 pak mod".</summary>
    [ObservableProperty]
    private string _pendingChangesSummary = "";

    [ObservableProperty]
    private bool _hasPendingPakChanges;

    private bool _mergeQueueSubscribed;

    private MergeInstallViewModel? TryGetMergeInstall()
    {
        try
        {
            return _mergeInstallViewModel();
        }
        catch (Exception)
        {
            // A test harness without a Merge & Install page: the "On" checkbox just has no queue to talk to.
            return null;
        }
    }

    private void EnsureMergeQueueSubscription()
    {
        if (_mergeQueueSubscribed)
        {
            return;
        }

        var mergeInstall = TryGetMergeInstall();
        if (mergeInstall is null)
        {
            return;
        }

        _mergeQueueSubscribed = true;
        mergeInstall.Queue.CollectionChanged += (_, _) => RefreshEnabledAndStatus();
        mergeInstall.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MergeInstallViewModel.LastInstalledModNames) or nameof(MergeInstallViewModel.ChangedSinceInstall))
            {
                RefreshEnabledAndStatus();
            }
        };
        _ = mergeInstall.RefreshInstalledModNamesAsync();
    }

    /// <summary>The user ticked/unticked a pak or EXMOD row: that IS adding it to / removing it from the merge queue; a mod's Lua half follows the same tick.</summary>
    private void OnItemEnabledToggled(object? sender, bool enabled)
    {
        if (sender is not LibraryItemViewModel item)
        {
            return;
        }

        var mergeInstall = TryGetMergeInstall();
        if (mergeInstall is null)
        {
            return;
        }

        if (enabled)
        {
            mergeInstall.AddToQueueByFolderNames([item.FolderName]);
            if (!mergeInstall.IsQueued(item.FolderName))
            {
                // Refused (already covered by a queued merged pack, for instance) — the queue says why.
                item.SetEnabledFromQueue(false);
                StatusMessage = mergeInstall.StatusMessage;
            }
        }
        else
        {
            mergeInstall.RemoveFromQueueByFolderNames([item.FolderName]);
        }

        // Refused either way (a build is running, or ISL's own pack can't be ticked): the tick goes
        // back, the Lua half stays as it was, and the queue's reason shows.
        if (mergeInstall.IsQueued(item.FolderName) != enabled)
        {
            StatusMessage = mergeInstall.StatusMessage;
            RefreshEnabledAndStatus();
            return;
        }

        foreach (var luaRow in LinkedLuaRows(item))
        {
            luaRow.IsEnabled = enabled;
        }

        RefreshEnabledAndStatus();
    }

    /// <summary>
    /// Recomputes every row's "On" tick (from the merge queue) and Status (from the installed
    /// merged pak's manifest plus the Lua half's own state), and the pending-changes bar.
    /// </summary>
    private void RefreshEnabledAndStatus()
    {
        var mergeInstall = TryGetMergeInstall();
        var installed = new HashSet<string>(mergeInstall?.LastInstalledModNames ?? [], StringComparer.OrdinalIgnoreCase);
        var changedSinceInstall = mergeInstall?.ChangedSinceInstall ?? new Dictionary<string, string>();
        var pendingPak = 0;
        var updatedCount = 0;
        var inGameNotTicked = 0;
        var pendingDataChanges = new List<(string Name, bool TurnedOn)>();
        foreach (var item in _itemsByFolderName.Values)
        {
            var queued = mergeInstall?.IsQueued(item.FolderName) ?? false;
            item.SetEnabledFromQueue(queued);

            // The manifest lists an EXMOD by name and an opaque entry by each pak's file name.
            var inGame = item.IsOpaquePak && item.PakFileNames.Count > 0
                ? item.PakFileNames.All(p => installed.Contains(Path.GetFileNameWithoutExtension(p)))
                : installed.Contains(item.Name) || installed.Contains(item.FolderName);

            var luaRows = LinkedLuaRows(item).ToList();
            var luaDirty = luaRows.Any(r => r.IsDirty);
            var luaInGame = luaRows.Count > 0 && luaRows.All(r => r.RealIsEnabled);

            string status;
            var detail = "";
            if (luaDirty)
            {
                status = "Needs Apply";
                detail = queued && !inGame ? "then Merge & Install for the paks" : "";
            }
            else if (queued && !inGame)
            {
                status = "Needs Merge & Install";
                detail = luaRows.Count > 0 ? (luaInGame ? "scripts in game · paks not yet" : "paks not in the game yet") : "";
            }
            else if (!queued && inGame)
            {
                // Still in the game but unticked: a green "In game" read as if nothing would change.
                status = "Will be removed";
                detail = "not ticked: Merge & Install takes it out";
            }
            else if (queued && inGame && changedSinceInstall.TryGetValue(item.FolderName, out var change))
            {
                // Changed (in Starlink or outside it) since the game's merge was built: the game has the old copy.
                status = "Changed";
                detail = $"{change} · needs Merge & Install";
            }
            else if (queued && inGame)
            {
                status = "In game";
                detail = luaRows.Count > 0 && !luaInGame ? "paks in game · scripts not enabled" : "";
            }
            else
            {
                status = "Not enabled";
            }

            var updated = queued && inGame && changedSinceInstall.ContainsKey(item.FolderName);
            item.IsPendingChange = queued != inGame || updated;
            if (queued != inGame)
            {
                pendingPak++;
                pendingDataChanges.Add((item.Name, queued));
            }
            else if (updated)
            {
                updatedCount++;
            }

            if (inGame && !queued && !item.IsMergedPack)
            {
                inGameNotTicked++;
            }

            item.InstallStatus = status;
            item.InstallStatusDetail = detail;
        }

        var pendingLua = Ue4ssMods.Count(m => m.IsDirty);
        InGameNotTickedCount = inGameNotTicked;
        // ArcFission Library rebuilt its data since the merge: its new rows need a Merge & Install.
        var arcFission = mergeInstall?.ArcFissionNeedsMerge == true;
        HasPendingPakChanges = pendingPak > 0 || updatedCount > 0 || arcFission;
        HasPendingChanges = HasPendingPakChanges || pendingLua > 0;
        var parts = new List<string>();
        if (pendingLua > 0)
        {
            parts.Add(pendingLua == 1 ? "1 UE4SS change" : $"{pendingLua} UE4SS changes");
        }

        if (pendingPak > 0)
        {
            parts.Add(pendingPak == 1 ? "1 pak mod" : $"{pendingPak} pak mods");
        }

        if (updatedCount > 0)
        {
            parts.Add(updatedCount == 1 ? "1 changed mod" : $"{updatedCount} changed mods");
        }

        if (arcFission)
        {
            parts.Add("ArcFission's data (WirelessPower and other ArcFission mods) changed since the last merge");
        }

        PendingChangesSummary = parts.Count == 0
            ? ""
            : $"Changes waiting: {string.Join(" and ", parts)}";
        var updatedNames = _itemsByFolderName.Values
            .Where(i => i.InstallStatus == "Changed")
            .Select(i => i.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var updatedText = updatedNames.Count == 0 ? "" : $"Changed since your last Merge & Install: {string.Join(", ", updatedNames)}";
        var dataText = arcFission && pendingDataChanges.Count == 0
            ? "ArcFission's data changed since the last merge"
            : DescribePending([.. pendingDataChanges.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)]);
        PendingDataText = string.Join("\n", new[] { dataText, updatedText }.Where(t => t.Length > 0));
        PendingUe4ssText = DescribePending([.. Ue4ssMods.Where(m => m.IsDirty).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => (m.Name, m.IsEnabled))]);

        // A ticked Lua mod (on its own, or the Lua half of a pak mod) with no UE4SS in the game can't run.
        ShowUe4ssLoaderMissing = !IsUe4ssLoaderInstalled
            && (Ue4ssMods.Any(m => m.IsEnabled) || _itemsByFolderName.Values.Any(i => i.IsEnabled && i.LinkedUe4ssNames.Count > 0));
    }

    /// <summary>Whether the game has the UE4SS loader. True until the first read, and when no game folder is set, so nothing warns before there's anything to check.</summary>
    [ObservableProperty]
    private bool _isUe4ssLoaderInstalled = true;

    /// <summary>The "UE4SS isn't installed" bar under the list.</summary>
    [ObservableProperty]
    private bool _showUe4ssLoaderMissing;

    /// <summary>"3.0.1 dev build 1181": the build the missing-loader bar's Install button puts in.</summary>
    public string BundledUe4ssVersion => BundledUe4ss.Label;

    /// <summary>The bar's Install button: Settings owns installing the loader (confirmation, game-running check, backup), so it is asked to do it.</summary>
    [RelayCommand]
    private void InstallUe4ssLoader() => WeakReferenceMessenger.Default.Send(new InstallUe4ssRequestedMessage());

    /// <summary>The pending bar's "Merge &amp; Install" button — hands over to that page rather than building here, since conflicts are reviewed there.</summary>
    [RelayCommand]
    private void GoToMergeInstall() => WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("merge"));

    /// <summary>Mods the game has (from the last Merge &amp; Install) that aren't ticked: Merge &amp; Install would take them out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTickWhatsInTheGame), nameof(TickWhatsInTheGameLabel), nameof(ShowPendingBar))]
    private int _inGameNotTickedCount;

    /// <summary>Several in-game mods unticked at once: most likely the ticks were lost, not undone one by one.</summary>
    public bool CanTickWhatsInTheGame => InGameNotTickedCount > 1;

    public string TickWhatsInTheGameLabel => $"Tick the {InGameNotTickedCount} mods in the game";

    /// <summary>Ticks again what the game's merged pak was built from, in its merge order.</summary>
    [RelayCommand]
    private void TickWhatsInTheGame()
    {
        var mergeInstall = TryGetMergeInstall();
        var ticked = mergeInstall?.RestoreQueueFromGame() ?? 0;
        StatusMessage = ticked > 0
            ? $"Ticked the {ticked} mod(s) that are in the game, in the order they were merged. Nothing changes in the game until Merge & Install."
            : "Couldn't find the mods the game's merged pak was built from in your Library.";
        RefreshEnabledAndStatus();
    }
}
