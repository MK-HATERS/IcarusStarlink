using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One row in Library's UE4SS tab. IsEnabled is the PENDING checkbox state — starts equal to
/// RealIsEnabled (what's actually true right now) and only diverges once the user toggles it;
/// clicking Apply is what actually moves the mod and syncs RealIsEnabled back to match. IsDirty
/// drives whether the page-level Apply button is enabled at all.
///
/// NexusModId/KnownVersion/MinUe4ssVersion come from the separate Ue4ss_Meta sidecar store (a UE4SS
/// mod carries no metadata file of its own, unlike a Library EXMOD entry) — set once, at the moment
/// a UE4SS mod is downloaded through this app's own Nexus pipeline (NexusModId/KnownVersion only) or
/// via the row's own context-menu prompts; a mod staged/imported any other way just has no link/no
/// declared minimum, same as a manually-imported Library mod has no Source. LatestVersion is
/// populated by LibraryViewModel.CheckForUpdatesAsync, mirroring LibraryItemViewModel's own
/// HasUpdateAvailable shape exactly.
/// </summary>
public sealed partial class Ue4ssModRowViewModel : ObservableObject
{
    private readonly Action _onDirtyChanged;

    /// <summary>The installed UE4SS loader's own version (Ue4ssLoaderStatus.InstalledVersion) at the moment this row was built — shared across every row in a given reload, not per-mod.</summary>
    private readonly string? _installedLoaderVersion;

    public Ue4ssModRowViewModel(
        string name, bool realIsEnabled, bool isBuiltIn, int? nexusModId, string? knownVersion,
        string? minUe4ssVersion, string? installedLoaderVersion, Action onDirtyChanged)
    {
        Name = name;
        RealIsEnabled = realIsEnabled;
        _isEnabled = realIsEnabled;
        IsBuiltIn = isBuiltIn;
        NexusModId = nexusModId;
        KnownVersion = knownVersion;
        MinUe4ssVersion = minUe4ssVersion;
        _installedLoaderVersion = installedLoaderVersion;
        _onDirtyChanged = onDirtyChanged;
    }

    public string Name { get; }

    public bool RealIsEnabled { get; }

    /// <summary>Whether this is one of UE4SS's own bundled mods (or its shared\ infrastructure folder) rather than something the user installed themselves — see IUe4ssLoaderInstallService.IsFrameworkOwned. Drives the "Default" badge, and whether Link to Nexus even makes sense to offer at all (a framework mod has no standalone Nexus page of its own).</summary>
    public bool IsBuiltIn { get; }

    /// <summary>Computed inverse, not a converter parameter on IsBuiltIn's own binding — this project's own established convention after repeated ConverterParameter-on-BoolToVisibilityConverter mistakes.</summary>
    public bool IsUserAdded => !IsBuiltIn;

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

    /// <summary>
    /// User-entered minimum UE4SS loader version this mod needs (e.g. "3.0.1") — set via this row's
    /// own "Set minimum UE4SS version…" context-menu prompt (LibraryViewModel.SetMinUe4ssVersion),
    /// mirroring NexusModId's sidecar-backed shape exactly. Never inferred automatically (see
    /// Ue4ssModMeta.MinUe4ssVersion's own doc comment for why); null means the user hasn't declared
    /// one, which is never treated as a warning.
    /// </summary>
    public string? MinUe4ssVersion { get; }

    /// <summary>
    /// Ue4ssVersionComparer's verdict for MinUe4ssVersion against the installed loader version this
    /// row was built with. Unknown whenever there's no declared minimum, no installed version is
    /// known, or either string isn't a clean dotted-numeric version this can confidently compare —
    /// see Ue4ssVersionComparer's own doc comment for why that degrades rather than guesses.
    /// </summary>
    public Ue4ssVersionCompatibility VersionCompatibility => Ue4ssVersionComparer.Compare(MinUe4ssVersion, _installedLoaderVersion);

    /// <summary>Drives the row's warning icon — true only when a declared minimum genuinely exceeds what's actually installed, never for Unknown.</summary>
    public bool HasVersionWarning => VersionCompatibility == Ue4ssVersionCompatibility.BelowMinimum;

    public string? VersionWarningTooltip => HasVersionWarning
        ? $"Declares a minimum UE4SS v{MinUe4ssVersion}, but the installed loader is v{_installedLoaderVersion} — this mod may not work correctly until UE4SS is updated."
        : null;

    [ObservableProperty]
    private bool _isEnabled;

    public bool IsDirty => IsEnabled != RealIsEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDirty));
        _onDirtyChanged();
    }
}
