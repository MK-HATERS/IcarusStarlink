using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One UE4SS row in Library's single Mods list. IsEnabled is the PENDING checkbox state — starts
/// equal to RealIsEnabled (what's actually true right now) and only diverges once the user toggles
/// it; Apply UE4SS changes (on the pending bar) is what actually moves the mod and syncs
/// RealIsEnabled back to match. IsDirty drives whether the pending bar offers that button.
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
        string? minUe4ssVersion, string? installedLoaderVersion, Action onDirtyChanged, string? nexusName = null)
    {
        Name = name;
        NexusName = nexusName;
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

    /// <summary>What a screen reader says for the UE4SS row (the class name otherwise).</summary>
    public override string ToString() => Name;

    public bool RealIsEnabled { get; }

    // ---- The single Mods list: UE4SS rows sit in the same list as pak/EXMOD rows. ----

    /// <summary>The Type column.</summary>
    public string TypeLabel => "UE4SS";

    /// <summary>"Needs Apply" while the checkbox differs from what's on disk, "In game" when UE4SS starts it, "Off in UE4SS" when its folder is in the game but nothing starts it, else "Not enabled".</summary>
    public string InstallStatus => LoaderMissing && IsEnabled ? "Needs UE4SS" : IsDirty ? "Needs Apply" : RealIsEnabled ? "In game" : IsSwitchedOffInGame ? "Off in UE4SS" : "Not enabled";

    public string InstallStatusDetail => LoaderMissing && IsEnabled
        ? "UE4SS itself isn't installed in the game, so this mod can't run. Install it from the bar below the list or in Settings."
        : IsDirty ? (IsEnabled ? (IsSwitchedOffInGame ? "ticked — click Apply to turn it on (its enabled.txt)" : "ticked — click Apply to put it in the game") : "unticked — click Apply to take it out")
        : IsSwitchedOffInGame ? $"Its folder is in the game, but UE4SS doesn't start it: {NotStartedReason}. Tick it and Apply to turn it on."
        : "";

    /// <summary>Set by LibraryViewModel for a mod whose folder is in the game but that UE4SS doesn't start: why (Ue4ssModStart.WhyNotStarted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitchedOffInGame), nameof(InstallStatus), nameof(InstallStatusDetail), nameof(NeedsAction))]
    private string? _notStartedReason;

    /// <summary>In the game folder, doing nothing: UE4SS doesn't start it.</summary>
    public bool IsSwitchedOffInGame => NotStartedReason is not null;

    /// <summary>Its folder is in the game's Mods folder (started or not), not in Starlink's staging: where its files are.</summary>
    public bool IsFolderInGame => RealIsEnabled || IsSwitchedOffInGame;

    /// <summary>Set by LibraryViewModel: the game has no UE4SS loader (dwmapi.dll + ue4ss\UE4SS.dll), so a ticked Lua mod can't run.</summary>
    [ObservableProperty]
    private bool _loaderMissing;

    partial void OnLoaderMissingChanged(bool value)
    {
        OnPropertyChanged(nameof(InstallStatus));
        OnPropertyChanged(nameof(InstallStatusDetail));
        OnPropertyChanged(nameof(IsInGame));
        OnPropertyChanged(nameof(NeedsAction));
    }

    public bool IsInGame => InstallStatus == "In game";

    /// <summary>Set by LibraryViewModel: the mod ships a config file (config.lua, config.ini…), so the right-click menu offers Edit config.</summary>
    [ObservableProperty]
    private bool _hasConfig;

    /// <summary>Set by LibraryViewModel: the last update added settings to its config or dropped some (Ue4ssModMeta.ConfigReviews), and the user hasn't looked yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditConfigTooltip))]
    private bool _configNeedsReview;

    public string EditConfigTooltip => ConfigNeedsReview
        ? "Edit config… The last update added or removed some settings: have a look"
        : "Edit config…";

    public bool NeedsAction => IsDirty || (LoaderMissing && IsEnabled) || IsSwitchedOffInGame;

    /// <summary>Whether this is one of UE4SS's own bundled mods (or its shared\ infrastructure folder) rather than something the user installed themselves — see IUe4ssLoaderInstallService.IsFrameworkOwned. Drives the "Default" badge, and whether Link to Nexus even makes sense to offer at all (a framework mod has no standalone Nexus page of its own).</summary>
    public bool IsBuiltIn { get; }

    /// <summary>Computed inverse, not a converter parameter on IsBuiltIn's own binding — this project's own established convention after repeated ConverterParameter-on-BoolToVisibilityConverter mistakes.</summary>
    public bool IsUserAdded => !IsBuiltIn;

    public int? NexusModId { get; }

    public bool HasNexusLink => NexusModId is not null;

    /// <summary>The Nexus mod page's title (Ue4ssModMeta.NexusName); null until a Nexus lookup has recorded it.</summary>
    public string? NexusName { get; }

    /// <summary>"Nexus: ArcFission Library · v1" — the line under the folder name that says what this staged mod actually is; null for an unlinked mod.</summary>
    public string? NexusSummary
    {
        get
        {
            if (!HasNexusLink)
            {
                return null;
            }

            var name = NexusName is null ? $"Nexus mod #{NexusModId}" : $"Nexus: {NexusName}";
            return KnownVersion is null ? name : $"{name} · v{KnownVersion}";
        }
    }

    /// <summary>The same "Open on Nexus" the Mods tab offers — a staged UE4SS mod has a page too.</summary>
    [RelayCommand(CanExecute = nameof(HasNexusLink))]
    private void OpenOnNexus()
    {
        if (NexusModId is { } modId)
        {
            UrlOpener.TryOpen(NexusModWebUrl.For(modId));
        }
    }

    /// <summary>Same shape as LibraryItemViewModel.NexusLinkActionLabel — an already-linked row is offered a re-link, not told to link.</summary>
    public string NexusLinkActionLabel => HasNexusLink ? "Re-link Nexus ID…" : "Link to Nexus ID…";

    /// <summary>The version this mod's own file was at when downloaded/linked — compared against LatestVersion to decide HasUpdateAvailable.</summary>
    public string? KnownVersion { get; }

    /// <summary>The version the mod's own Scripts\main.lua states (Ue4ssModVersion), set by LibraryViewModel; null when it states none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    private string? _localVersion;

    /// <summary>The Version column: the installed copy's own main.lua version, else the version recorded at download; empty when neither is known (every UE4SS built-in).</summary>
    public string VersionText => VersionLabel.For(LocalVersion ?? KnownVersion);

    /// <summary>This account's Nexus endorsement of the mod, from Check endorsements (or the row's own Endorse); null until known. Same as LibraryItemViewModel.EndorsementStatus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndorsementStatusDisplay), nameof(IsEndorsed))]
    private NexusEndorsementStatus? _endorsementStatus;

    public bool IsEndorsed => EndorsementStatus == NexusEndorsementStatus.Endorsed;

    public string EndorsementStatusDisplay => EndorsementStatus switch
    {
        NexusEndorsementStatus.Endorsed => "Endorsed",
        NexusEndorsementStatus.Abstained => "Not endorsing",
        NexusEndorsementStatus.Undecided => "Not endorsed yet",
        _ => "",
    };

    /// <summary>The data mod that is this mod's other half (MkHatersBags ↔ MkHaters Bags), set by LibraryViewModel; null when there is none. Only a hint in the list: each keeps its own tick.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedPartner))]
    private LibraryItemViewModel? _linkedPartner;

    public bool HasLinkedPartner => LinkedPartner is not null;

    /// <summary>The mouse is over the other half's row: this row lights up too.</summary>
    [ObservableProperty]
    private bool _isLinkHighlighted;

    [ObservableProperty]
    private string? _latestVersion;

    // KnownVersion can genuinely be null — linking records the Nexus ID unconditionally but the
    // version enrichment that fills KnownVersion is best-effort (see
    // DownloadsViewModel.EnrichUe4ssModFromNexusAsync) — without the IsNullOrEmpty(KnownVersion)
    // guard, a null KnownVersion never equals any real fetched LatestVersion, permanently
    // mislabeling an unenriched mod as having an update available.
    // Not a plain string compare — see UpdateVersionComparer ("1" vs "1.0" is the same version).
    public bool HasUpdateAvailable =>
        HasNexusLink && (NexusFileId is { } installedFile && LatestNexusFileId is { } newestFile
            ? newestFile != installedFile
            : !string.IsNullOrEmpty(KnownVersion) && UpdateVersionComparer.HasUpdate(LatestVersion, KnownVersion, null));

    /// <summary>The Nexus file this mod came from (Ue4ssModMeta.NexusFileId); when known, updates follow that file's own update chain.</summary>
    public int? NexusFileId { get; set; }

    /// <summary>The newest file in that chain, from the last check.</summary>
    [ObservableProperty]
    private int? _latestNexusFileId;

    partial void OnLatestNexusFileIdChanged(int? value) => OnPropertyChanged(nameof(HasUpdateAvailable));

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
        ? $"Needs UE4SS v{MinUe4ssVersion} or newer, but you have v{_installedLoaderVersion}. It may not work right until you update UE4SS."
        : null;

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>Part of the Mods list's click selection (grey row): Ctrl/Shift-click and Select all include UE4SS rows, and ticking ON on a selected row ticks every selected row.</summary>
    [ObservableProperty]
    private bool _isSelectedForBulk;

    public bool IsDirty => IsEnabled != RealIsEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(InstallStatus));
        OnPropertyChanged(nameof(InstallStatusDetail));
        OnPropertyChanged(nameof(IsInGame));
        OnPropertyChanged(nameof(NeedsAction));
        _onDirtyChanged();
    }
}
