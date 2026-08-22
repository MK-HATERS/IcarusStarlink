using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Catalog;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One tracked Nexus mod — Name is the only editable field (Nexus's own mod name isn't fetchable without API access, so the user names it themselves) and saves on edit, debounced the same 500ms way LibraryItemViewModel debounces Notes so the whole watchlist file isn't rewritten on every keystroke.</summary>
public sealed partial class NexusWatchlistItemViewModel : ObservableObject
{
    private readonly Action<int, string> _onNameChanged;
    private readonly DebounceTimer _nameSaveDebounceTimer;

    public int NexusId { get; }
    public string Url { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Set by DownloadsViewModel.CheckForNexusUpdates — not persisted (matches
    /// NexusWatchlistEntry's own "computed at display time" philosophy for IsDownloaded), and
    /// reset every time NexusEntries is rebuilt (e.g. the filter box changing), so a re-check is
    /// needed after that. True means this mod's own Nexus ID appeared in Nexus's own
    /// recently-updated list (period=1m) — a real but coarse signal, not an exact version diff.
    /// </summary>
    [ObservableProperty]
    private bool _hasUpdateAvailable;

    public NexusWatchlistItemViewModel(NexusWatchlistEntry entry, Action<int, string> onNameChanged)
    {
        NexusId = entry.NexusId;
        Url = entry.Url;
        _name = entry.Name;
        _onNameChanged = onNameChanged;

        _nameSaveDebounceTimer = new DebounceTimer(TimeSpan.FromMilliseconds(500), () => _onNameChanged(NexusId, Name));
    }

    /// <summary>
    /// Stops any pending debounced name save without flushing it — mirrors
    /// LibraryItemViewModel.CancelPendingSave, called right before this entry is removed from the
    /// watchlist so a stale timer firing after Remove() can't write this entry's old name back
    /// into the store under a NexusId a later Add() might reuse.
    /// </summary>
    public void CancelPendingSave() => _nameSaveDebounceTimer.Cancel();

    /// <summary>
    /// Call before rebuilding the watchlist collection from a store reload (the entry itself isn't
    /// being removed from the store) — unlike CancelPendingSave, saves a pending edit immediately
    /// rather than discarding it, so a name the user just typed isn't silently lost just because a
    /// reload happened to run before the debounce elapsed.
    /// </summary>
    public void FlushPendingSave() => _nameSaveDebounceTimer.Flush();

    partial void OnNameChanged(string value) => _nameSaveDebounceTimer.Restart();
}
