using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Nexus page — two modes over one nav item since W1: "Browse" (the native, API-key-driven
/// mod list, see NexusCatalogViewModel) and "Full site" (Phase 8.3a's embedded WebView2 browser,
/// still the only way to search or start a real Mod Manager Download). Mode is a Visibility
/// toggle, deliberately NOT a TabControl: a deselected tab's content leaves the visual tree,
/// which would fire this view's own Unloaded-dispose on the WebView2 with no re-init path — and
/// WebView2's HwndHost has its own well-known detach quirks a plain Collapsed never hits.
/// The WebView2 control itself stays handled entirely in NexusBrowserView's code-behind, same
/// UI-only-concern reasoning as before.
/// </summary>
public sealed partial class NexusBrowserViewModel : ObservableObject
{
    public const string HomeUrl = "https://www.nexusmods.com/icarus";

    public string Title => "Nexus";

    /// <summary>The native Browse half's own state — exposed for the view's DataContext binding rather than resolved separately, so this page stays one nav item with one ViewModel entry point.</summary>
    public NexusCatalogViewModel Catalog { get; }

    /// <summary>Where WebView2 keeps its own browser profile (cookies, so the user's real Nexus login persists across app restarts) — this app's own data folder, not WebView2's default location, matching how every other piece of this app's own data is kept in one predictable place.</summary>
    public string UserDataFolder { get; }

    [ObservableProperty]
    private bool _isBrowseMode = true;

    public bool IsFullSiteMode => !IsBrowseMode;

    partial void OnIsBrowseModeChanged(bool value) => OnPropertyChanged(nameof(IsFullSiteMode));

    [RelayCommand]
    private void ShowBrowse() => IsBrowseMode = true;

    [RelayCommand]
    private void ShowFullSite() => IsBrowseMode = false;

    public NexusBrowserViewModel(NexusCatalogViewModel catalog, string userDataFolder)
    {
        Catalog = catalog;
        UserDataFolder = userDataFolder;
    }
}
