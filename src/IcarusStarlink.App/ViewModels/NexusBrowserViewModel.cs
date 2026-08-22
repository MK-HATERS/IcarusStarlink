using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 8.3a: the embedded Nexus browser page — deliberately thin. The WebView2 control itself
/// (navigation, back/forward/reload, the missing-runtime fallback) is handled entirely in
/// NexusBrowserView's own code-behind, the same way this app already treats other genuinely
/// UI-only concerns (ExmodEditorWindow's Ctrl+F focus, LibraryView's TreeView selection) — there's
/// nothing here worth routing through bindings just to keep the ViewModel "pure".
/// </summary>
public sealed partial class NexusBrowserViewModel : ObservableObject
{
    public const string HomeUrl = "https://www.nexusmods.com/icarus";

    public string Title => "Nexus";

    /// <summary>Where WebView2 keeps its own browser profile (cookies, so the user's real Nexus login persists across app restarts) — this app's own data folder, not WebView2's default location, matching how every other piece of this app's own data is kept in one predictable place.</summary>
    public string UserDataFolder { get; }

    public NexusBrowserViewModel(string userDataFolder)
    {
        UserDataFolder = userDataFolder;
    }
}
