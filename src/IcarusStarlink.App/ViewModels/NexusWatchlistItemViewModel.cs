using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Catalog;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One tracked Nexus mod — Name is the only editable field (Nexus's own mod name isn't fetchable without API access, so the user names it themselves) and saves on edit, debounced the same 500ms way LibraryItemViewModel debounces Notes so the whole watchlist file isn't rewritten on every keystroke.</summary>
public sealed partial class NexusWatchlistItemViewModel : ObservableObject
{
    private readonly Action<int, string> _onNameChanged;
    private readonly DispatcherTimer _nameSaveDebounceTimer;

    public int NexusId { get; }
    public string Url { get; }

    [ObservableProperty]
    private string _name;

    public NexusWatchlistItemViewModel(NexusWatchlistEntry entry, Action<int, string> onNameChanged)
    {
        NexusId = entry.NexusId;
        Url = entry.Url;
        _name = entry.Name;
        _onNameChanged = onNameChanged;

        _nameSaveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _nameSaveDebounceTimer.Tick += (_, _) =>
        {
            _nameSaveDebounceTimer.Stop();
            _onNameChanged(NexusId, Name);
        };
    }

    /// <summary>
    /// Stops any pending debounced name save without flushing it — mirrors
    /// LibraryItemViewModel.CancelPendingSave, called right before this entry is removed from the
    /// watchlist so a stale timer firing after Remove() can't write this entry's old name back
    /// into the store under a NexusId a later Add() might reuse.
    /// </summary>
    public void CancelPendingSave() => _nameSaveDebounceTimer.Stop();

    partial void OnNameChanged(string value)
    {
        _nameSaveDebounceTimer.Stop();
        _nameSaveDebounceTimer.Start();
    }
}
