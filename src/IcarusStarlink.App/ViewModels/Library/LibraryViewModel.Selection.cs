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

/// <summary>The click selection: plain/Ctrl/Shift-click, Select all, and turning the selected mods on or off.</summary>
public sealed partial class LibraryViewModel
{
    private static bool IsRowSelected(object row) => row switch
    {
        LibraryItemViewModel item => item.IsSelectedForBulk,
        Ue4ssModRowViewModel lua => lua.IsSelectedForBulk,
        _ => false,
    };

    private void SetRowSelected(object row, bool selected)
    {
        switch (row)
        {
            case LibraryItemViewModel item when item.IsSelectedForBulk != selected:
                item.IsSelectedForBulk = selected;
                if (selected)
                {
                    BulkSelectedItems.Add(item);
                }
                else
                {
                    BulkSelectedItems.Remove(item);
                }

                break;
            case Ue4ssModRowViewModel lua when lua.IsSelectedForBulk != selected:
                lua.IsSelectedForBulk = selected;
                if (selected)
                {
                    BulkSelectedUe4ssRows.Add(lua);
                }
                else
                {
                    BulkSelectedUe4ssRows.Remove(lua);
                }

                break;
        }
    }

    /// <summary>Ctrl-click: adds or removes this one row (pak or UE4SS) and makes it the Shift-click anchor.</summary>
    public void ToggleBulkSelection(object row)
    {
        SetRowSelected(row, !IsRowSelected(row));
        _bulkSelectionAnchor = row;
    }

    public void ClearBulkSelection()
    {
        foreach (var item in BulkSelectedItems)
        {
            item.IsSelectedForBulk = false;
        }

        foreach (var lua in BulkSelectedUe4ssRows)
        {
            lua.IsSelectedForBulk = false;
        }

        BulkSelectedItems.Clear();
        BulkSelectedUe4ssRows.Clear();
    }

    /// <summary>Plain click (no modifier): selects just this row (it turns grey) and makes it the anchor for a later Shift-click, like Explorer and Mod Organizer.</summary>
    public void SetBulkSelectionAnchor(object row)
    {
        ClearBulkSelection();
        SetRowSelected(row, true);
        _bulkSelectionAnchor = row;
    }

    /// <summary>Right-click: a row that's already part of a multi-selection stays part of it (so right-clicking any one of several Ctrl-selected rows adds all of them); right-clicking a row outside the current selection replaces it with just that row — the same convention Explorer uses.</summary>
    public void EnsureBulkSelected(object row)
    {
        if (!IsRowSelected(row))
        {
            ClearBulkSelection();
            SetRowSelected(row, true);
        }

        _bulkSelectionAnchor = row;
    }

    /// <summary>
    /// Ticking or unticking ON on a row that is part of a multi-selection applies the same state
    /// to every selected row: paks/EXMODs join or leave the merge queue, UE4SS mods are ticked for
    /// the next Apply. Returns how many other rows changed.
    /// </summary>
    public int ApplyEnabledToSelection(object clickedRow, bool enabled)
    {
        if (!IsRowSelected(clickedRow) || BulkSelectedCount < 2)
        {
            return 0;
        }

        var changed = 0;
        foreach (var item in BulkSelectedItems.ToList().Where(i => !ReferenceEquals(i, clickedRow) && i.IsEnabled != enabled))
        {
            item.IsEnabled = enabled;
            changed++;
        }

        // An always-on row (UE4SS's shared\) keeps its tick, and isn't counted as changed.
        foreach (var lua in BulkSelectedUe4ssRows.ToList().Where(r => !ReferenceEquals(r, clickedRow) && r.IsEnabled != enabled && !r.IsAlwaysOn))
        {
            lua.IsEnabled = enabled;
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// RootItems in visible order, with any variant family's own children expanded inline — the
    /// same flattened order Shift-click range-select and Select all both need. Deliberately doesn't
    /// consult the TreeView's own live expand/collapse state (fragile to reach for from a
    /// ViewModel, and TreeView-specific) — a range or "select all" acts on every real mod
    /// regardless of whether its family group happens to be collapsed right now, matching how this
    /// app's own bulk actions (Add to merge queue, Delete) already treat a family as a transparent
    /// container rather than something that can hide its members from a bulk operation.
    /// </summary>
    private List<object> FlattenRows() => [.. ExpandGroups(RootItems)];

    private List<LibraryItemViewModel> FlattenModItems() => [.. ExpandGroups(RootItems).OfType<LibraryItemViewModel>()];

    /// <summary>The mod rows (pak/EXMOD and UE4SS) in order, each variant family's mods in place of the family; download stubs dropped.</summary>
    private static IEnumerable<object> ExpandGroups(IEnumerable<object> rows) =>
        rows.SelectMany(entry => entry switch
        {
            LibraryItemViewModel or Ue4ssModRowViewModel => (IEnumerable<object>)[entry],
            LibraryGroupViewModel group => group.Items,
            _ => [],
        });

    /// <summary>Used by ModDetailWindow's own Prev/Next navigation — the same flattened visible
    /// order FlattenModItems already produces for Shift-click/Select all, so "next" matches
    /// whatever's actually currently shown (respecting the active search/sort), not some other
    /// ordering. Null at either end of the list rather than wrapping around.</summary>
    public LibraryItemViewModel? GetAdjacentItem(LibraryItemViewModel current, int direction)
    {
        var flattened = FlattenModItems();
        var index = flattened.IndexOf(current);
        if (index < 0)
        {
            return null;
        }

        var targetIndex = index + direction;
        return targetIndex >= 0 && targetIndex < flattened.Count ? flattened[targetIndex] : null;
    }

    /// <summary>Shift-click: selects every mod between the last plain/Ctrl-clicked anchor and this one (inclusive), replacing whatever was selected before — matching Explorer's own Shift-click convention. Falls back to selecting just this row alone if there's no anchor yet (e.g. the very first click in a fresh session was a Shift-click).</summary>
    public void SelectBulkRange(object target)
    {
        var flattened = FlattenRows();
        // A range stays inside one panel: an anchor in the other panel counts as none.
        var anchor = _bulkSelectionAnchor is not null && (_bulkSelectionAnchor is Ue4ssModRowViewModel) == (target is Ue4ssModRowViewModel) ? _bulkSelectionAnchor : null;
        var anchorIndex = anchor is not null ? flattened.IndexOf(anchor) : -1;
        var targetIndex = flattened.IndexOf(target);
        if (targetIndex < 0)
        {
            return;
        }

        if (anchorIndex < 0)
        {
            anchorIndex = targetIndex;
        }

        ClearBulkSelection();
        var (start, end) = anchorIndex <= targetIndex ? (anchorIndex, targetIndex) : (targetIndex, anchorIndex);
        for (var i = start; i <= end; i++)
        {
            SetRowSelected(flattened[i], true);
        }

        // Deliberately NOT updated to target — Explorer's own Shift-click keeps ranging from the
        // original anchor on a repeated Shift-click, so shrinking/growing a range with successive
        // Shift-clicks stays possible instead of the anchor chasing the most recent click.
    }

    /// <summary>
    /// "Select all" (context menu, Ctrl+A) — every mod currently shown in ONE panel ("data" or
    /// "ue4ss"; respects the search filter). It used to select both panels, so Ctrl+A then Delete
    /// in the UE4SS panel offered to delete every data mod (review, 2026-10-23).
    /// </summary>
    [RelayCommand]
    private void SelectAllForBulk(string? panel)
    {
        ClearBulkSelection();
        foreach (var row in ExpandGroups(panel == "ue4ss" ? Ue4ssRootItems : DataRootItems).ToList())
        {
            SetRowSelected(row, true);
        }

        _bulkSelectionAnchor = null;
    }

    /// <summary>The selection bar's Turn on / Turn off ("data" or "ue4ss" panel): every selected mod in that panel, as if each ON box were ticked.</summary>
    [RelayCommand]
    private void TurnOnSelected(string? panel) => SetSelectedEnabled(panel, true);

    [RelayCommand]
    private void TurnOffSelected(string? panel) => SetSelectedEnabled(panel, false);

    /// <summary>Space in the list: turns the selection on, or off when every selected mod is already on.</summary>
    public void ToggleSelectedEnabled()
    {
        var states = BulkSelectedItems.Select(i => i.IsEnabled).Concat(BulkSelectedUe4ssRows.Select(r => r.IsEnabled)).ToList();
        if (states.Count == 0)
        {
            return;
        }

        var enable = !states.All(on => on);
        SetSelectedEnabled("data", enable);
        SetSelectedEnabled("ue4ss", enable);
    }

    private void SetSelectedEnabled(string? panel, bool enabled)
    {
        if (panel is null or "data")
        {
            foreach (var item in BulkSelectedItems.ToList().Where(i => i.IsEnabled != enabled))
            {
                item.IsEnabled = enabled;
            }
        }

        if (panel is null or "ue4ss")
        {
            foreach (var row in BulkSelectedUe4ssRows.ToList().Where(r => r.IsEnabled != enabled))
            {
                row.IsEnabled = enabled;
            }
        }
    }
}
