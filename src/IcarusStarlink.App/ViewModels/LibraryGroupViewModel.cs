using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A variant family header for the Library tree — only ever constructed for a real family (2+ variants); a lone entry is added to the tree directly as a LibraryItemViewModel instead.</summary>
public sealed partial class LibraryGroupViewModel(string displayName) : ObservableObject
{
    [ObservableProperty]
    private string _displayName = displayName;

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    /// <summary>
    /// Reload() reuses the same LibraryGroupViewModel instance across searches (keyed by the
    /// family's GroupKey) instead of constructing a new one every time, so the TreeView keeps
    /// this family's expanded/collapsed state — WPF ties container state to item identity, and a
    /// fresh instance would always start collapsed. The member list can still change (e.g. a
    /// search narrows which variants match), so it's refreshed in place here rather than fixed
    /// at construction — via the same targeted sync RootItems uses one level up, not a blanket
    /// Clear()+re-Add() (which would just as surely reset every child container's own selection
    /// state on every reload, even when membership didn't actually change).
    ///
    /// DisplayName is also an [ObservableProperty], not fixed at construction: VariantGrouping
    /// derives it from whichever member entry happens to be enumerated first for this group's
    /// key, so it can legitimately change between reloads too — e.g. if the member that supplied
    /// it gets deleted, the next reload computes it from a different remaining member. Without a
    /// real property-changed notification here, a reused (identity-stable) instance would keep
    /// showing the old text forever, since nothing else would prompt WPF to re-read it.
    /// </summary>
    public void Update(string displayName, IReadOnlyList<LibraryItemViewModel> items)
    {
        DisplayName = displayName;
        ObservableCollectionSync.SyncTo(Items, items);
    }
}
