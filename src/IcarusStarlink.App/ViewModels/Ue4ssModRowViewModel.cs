using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One row in Library's UE4SS tab. IsEnabled is the PENDING checkbox state — starts equal to
/// RealIsEnabled (what's actually true right now) and only diverges once the user toggles it;
/// clicking Apply is what actually moves the mod and syncs RealIsEnabled back to match. IsDirty
/// drives whether the page-level Apply button is enabled at all.
///
/// NexusModId/KnownVersion come from the separate Ue4ss_Meta sidecar store (a UE4SS mod carries no
/// metadata file of its own, unlike a Library EXMOD entry) — set once, at the moment a UE4SS mod is
/// downloaded through this app's own Nexus pipeline; a mod staged/imported any other way just has
/// no link, same as a manually-imported Library mod has no Source. LatestVersion is populated by
/// LibraryViewModel.CheckForUpdatesAsync, mirroring LibraryItemViewModel's own HasUpdateAvailable
/// shape exactly.
/// </summary>
public sealed partial class Ue4ssModRowViewModel : ObservableObject
{
    private readonly Action _onDirtyChanged;

    public Ue4ssModRowViewModel(string name, bool realIsEnabled, int? nexusModId, string? knownVersion, Action onDirtyChanged)
    {
        Name = name;
        RealIsEnabled = realIsEnabled;
        _isEnabled = realIsEnabled;
        NexusModId = nexusModId;
        KnownVersion = knownVersion;
        _onDirtyChanged = onDirtyChanged;
    }

    public string Name { get; }

    public bool RealIsEnabled { get; }

    public int? NexusModId { get; }

    public bool HasNexusLink => NexusModId is not null;

    /// <summary>The version this mod's own file was at when downloaded/linked — compared against LatestVersion to decide HasUpdateAvailable.</summary>
    public string? KnownVersion { get; }

    [ObservableProperty]
    private string? _latestVersion;

    // KnownVersion can genuinely be null — linking records the Nexus ID unconditionally but the
    // version enrichment that fills KnownVersion is best-effort (see
    // DownloadsViewModel.EnrichUe4ssModFromNexusAsync) — without the IsNullOrEmpty(KnownVersion)
    // guard, a null KnownVersion never equals any real fetched LatestVersion, permanently
    // mislabeling an unenriched mod as having an update available.
    public bool HasUpdateAvailable =>
        HasNexusLink && !string.IsNullOrEmpty(LatestVersion) && !string.IsNullOrEmpty(KnownVersion)
        && !string.Equals(LatestVersion, KnownVersion, StringComparison.OrdinalIgnoreCase);

    partial void OnLatestVersionChanged(string? value) => OnPropertyChanged(nameof(HasUpdateAvailable));

    [ObservableProperty]
    private bool _isEnabled;

    public bool IsDirty => IsEnabled != RealIsEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDirty));
        _onDirtyChanged();
    }
}
