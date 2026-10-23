using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Secrets;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One of the filter chips above the Library's two panels ("Problems 2").</summary>
public sealed partial class LibraryFilterChip(string key, string label, string kind) : ObservableObject
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    /// <summary>"accent" (Not in game yet), "danger" (Problems) or "" — how the chip is coloured while it has a count.</summary>
    public string Kind { get; } = kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    private int _count;

    public bool HasCount => Count > 0;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The Library as two panels (user's design, 2026-10-23): data mods (EXMOD/pak, put in the game by
/// Merge &amp; Install) on the left, UE4SS mods (turned on and off by Apply) on the right, each with
/// its own list, count and "not in the game yet" bar. A mod with both halves (MkHaters Bags and
/// MkHatersBags) stays two rows, one per panel, each with its own tick; hovering one lights up the
/// other. Above them: filter chips, and short-lived notices instead of status lines that stayed.
/// </summary>
public sealed partial class LibraryViewModel
{
    /// <summary>The left panel: downloads in progress, then pak/EXMOD rows and variant families.</summary>
    public ObservableCollection<object> DataRootItems { get; } = [];

    /// <summary>The right panel: UE4SS rows.</summary>
    public ObservableCollection<object> Ue4ssRootItems { get; } = [];

    /// <summary>Mods shown in each panel (a family counts each of its mods).</summary>
    [ObservableProperty]
    private int _dataShownCount;

    [ObservableProperty]
    private int _ue4ssShownCount;

    /// <summary>The right panel's text when it has no rows ("No UE4SS mods found.", "Set the Icarus Content folder…").</summary>
    [ObservableProperty]
    private string _ue4ssEmptyText = "";

    public bool IsUe4ssPanelEmpty => Ue4ssRootItems.Count == 0;

    public bool IsDataPanelEmpty => DataRootItems.Count == 0;

    // ---- Filter chips ----

    public const string FilterAll = "All";
    public const string FilterOn = "On";
    public const string FilterOff = "Off";
    public const string FilterNotInGame = "NotInGame";
    public const string FilterUpdates = "Updates";
    public const string FilterProblems = "Problems";
    public const string FilterFavourites = "Favourites";

    public IReadOnlyList<LibraryFilterChip> FilterChips { get; } =
    [
        new(FilterAll, "All", ""),
        new(FilterOn, "On", ""),
        new(FilterOff, "Off", ""),
        new(FilterNotInGame, "Not in game yet", "accent"),
        new(FilterUpdates, "Updates", ""),
        new(FilterProblems, "Problems", "danger"),
        new(FilterFavourites, "★ Favourites", ""),
    ];

    /// <summary>Which chip is on (one at a time; All shows everything the search finds).</summary>
    [ObservableProperty]
    private string _listFilter = FilterAll;

    partial void OnListFilterChanged(string value)
    {
        foreach (var chip in FilterChips)
        {
            chip.IsSelected = chip.Key == value;
        }

        Reload();
    }

    [RelayCommand]
    private void SelectFilter(string? key) => ListFilter = key is null || ListFilter == key ? FilterAll : key;

    private static bool MatchesListFilter(LibraryItemViewModel item, string filter) => filter switch
    {
        FilterOn => item.IsEnabled,
        FilterOff => !item.IsEnabled,
        FilterNotInGame => item.IsPendingChange,
        FilterUpdates => item.HasUpdateAvailable,
        FilterProblems => IsProblem(item),
        FilterFavourites => item.IsFavorite,
        _ => true,
    };

    private static bool MatchesListFilter(Ue4ssModRowViewModel row, string filter) => filter switch
    {
        FilterOn => row.IsEnabled,
        FilterOff => !row.IsEnabled,
        FilterNotInGame => row.NeedsAction,
        FilterUpdates => row.HasUpdateAvailable,
        FilterProblems => IsProblem(row),
        FilterFavourites => false,
        _ => true,
    };

    /// <summary>Something to look at: a check found it doesn't fit the game, or it replaces a high-risk game file. Medium risk alone isn't one.</summary>
    private static bool IsProblem(LibraryItemViewModel item) => item.HasModIssues || item.IsHighFileRisk;

    private static bool IsProblem(Ue4ssModRowViewModel row) => row.HasVersionWarning || (row.LoaderMissing && row.IsEnabled);

    /// <summary>Counts every chip over what the search finds (not over the chip filter itself, so each chip says what it would show).</summary>
    private void UpdateFilterCounts(IReadOnlyList<LibraryItemViewModel> items, IReadOnlyList<Ue4ssModRowViewModel> rows)
    {
        foreach (var chip in FilterChips)
        {
            chip.Count = items.Count(i => MatchesListFilter(i, chip.Key)) + rows.Count(r => MatchesListFilter(r, chip.Key));
        }
    }

    // ---- Each panel's "not in the game yet" bar ----

    /// <summary>"Lasers turned on" / "3 mods: A turned on, B turned off…" for the left panel's bar; empty when nothing is pending.</summary>
    [ObservableProperty]
    private string _pendingDataText = "";

    [ObservableProperty]
    private string _pendingUe4ssText = "";

    private static string DescribePending(IReadOnlyList<(string Name, bool TurnedOn)> changes)
    {
        if (changes.Count == 0)
        {
            return "";
        }

        var described = changes.Take(3).Select(c => $"{c.Name} turned {(c.TurnedOn ? "on" : "off")}");
        var text = string.Join(", ", described);
        return changes.Count > 3 ? $"{text} and {changes.Count - 3} more" : text;
    }

    // ---- Selection bar (per panel) ----

    public bool HasDataSelection => BulkSelectedItems.Count > 0;

    public bool HasUe4ssSelection => BulkSelectedUe4ssRows.Count > 0;

    public int DataSelectedCount => BulkSelectedItems.Count;

    public int Ue4ssSelectedCount => BulkSelectedUe4ssRows.Count;

    private void RaisePanelSelectionChanged()
    {
        OnPropertyChanged(nameof(HasDataSelection));
        OnPropertyChanged(nameof(HasUe4ssSelection));
        OnPropertyChanged(nameof(DataSelectedCount));
        OnPropertyChanged(nameof(Ue4ssSelectedCount));
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

    /// <summary>The selection bar's ✕ and Escape.</summary>
    [RelayCommand]
    private void ClearSelection() => ClearBulkSelection();

    // ---- Short-lived notices (status lines used to stay on the page until replaced) ----

    /// <summary>The newest result ("Imported 'X'.", "Applied 1 change.") shown for a few seconds in the page's corner.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>The notice is a failure or a warning (shown in the danger colour).</summary>
    [ObservableProperty]
    private bool _isNoticeWarning;

    private DebounceTimer? _noticeTimer;

    public static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(8);

    private void ShowNotice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Notice = message;
        IsNoticeWarning = LooksLikeAProblem(message);
        _noticeTimer ??= new DebounceTimer(NoticeDuration, () => Notice = null);
        _noticeTimer.Restart();
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    internal static bool LooksLikeAProblem(string message) =>
        message.Contains("fail", StringComparison.OrdinalIgnoreCase)
        || message.Contains("couldn't", StringComparison.OrdinalIgnoreCase)
        || message.Contains("can't", StringComparison.OrdinalIgnoreCase)
        || message.Contains("error", StringComparison.OrdinalIgnoreCase);

    partial void OnStatusMessageChanged(string? value) => ShowNotice(value);

    partial void OnUe4ssStatusMessageChanged(string? value) => ShowNotice(value);

    private void OnDownloadsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadsViewModel.PendingDownloadStatusMessage))
        {
            ShowNotice(Downloads.PendingDownloadStatusMessage);
        }
    }

    // ---- Linked halves (MkHaters Bags ↔ MkHatersBags) ----

    /// <summary>
    /// Pairs each UE4SS mod with the data mod that is its other half: the same Nexus mod, or the same
    /// name once spaces, case and "&amp;"/"and" are ignored (MkHaters Bags / MkHatersBags, Minimap &amp;
    /// Map Icons / MiniMapa's Nexus title "Minimap and Map Icons"). Only a hint for the eye.
    /// </summary>
    private void LinkHalves()
    {
        var items = _itemsByFolderName.Values.ToList();
        foreach (var item in items)
        {
            item.LinkedPartner = null;
        }

        foreach (var row in Ue4ssMods)
        {
            row.LinkedPartner = null;
            if (row.IsLinkedToLibraryMod)
            {
                continue; // shown inside its data row ("Pak + UE4SS"), not as a row of its own
            }

            var keys = new[] { LinkKey(row.Name), row.NexusName is null ? null : LinkKey(row.NexusName) };
            var partner = items.FirstOrDefault(i => row.NexusModId is { } id && i.NexusModId == id)
                ?? items.FirstOrDefault(i => keys.Contains(LinkKey(i.Name)));
            row.LinkedPartner = partner;
            if (partner is not null)
            {
                partner.LinkedPartner = row;
            }
        }
    }

    internal static string LinkKey(string name) =>
        new([.. name.Replace("&", "and", StringComparison.Ordinal).ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    // ---- Endorsing a UE4SS mod (data rows endorse themselves: LibraryItemViewModel.Endorse) ----

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task EndorseUe4ssMod(Ue4ssModRowViewModel? row)
    {
        if (row?.NexusModId is not { } modId || row.IsEndorsed)
        {
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            StatusMessage = "Sign in with your Nexus API key in Settings to endorse mods.";
            return;
        }

        try
        {
            var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", modId);
            if (info is null)
            {
                StatusMessage = $"Couldn't reach '{row.Name}' on Nexus.";
                return;
            }

            row.EndorsementStatus = await _nexusApiClient.SetEndorsementAsync(apiKey, "icarus", modId, info.Version, endorse: true);
            StatusMessage = $"Endorsed '{row.Name}' on Nexus.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't endorse '{row.Name}': {ex.Message}";
        }
    }
}
