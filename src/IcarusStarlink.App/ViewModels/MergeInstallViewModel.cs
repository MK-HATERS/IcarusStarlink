using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.InstallComparison;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.GameplayToggles;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Patches;
using IcarusStarlink.PakIO.Rebuild;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 6 (see the plan's "Update (2026-08-21)" section): Profile bar (6.3) + merge queue +
/// Rebuild (6.1) + Install (6.2). Rebuild only ever writes to a staged pak under IcarusStarlink's
/// own folder; Install is the one action in this whole app that writes into the real game's
/// Content\Paks\mods — deliberately its own separate, explicit button rather than folded into
/// Rebuild, so a click there is never accidental.
/// </summary>
public sealed partial class MergeInstallViewModel : ObservableObject
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IRebuildService _rebuildService;
    private readonly IInstallService _installService;
    private readonly IProfileStore _profileStore;
    private readonly IPatchService _patchService;
    private readonly IDaedalusCatalogClient _daedalusClient;
    private readonly IJimk72CatalogClient _jimk72Client;
    private readonly ISettingsService _settingsService;
    private readonly IPakCompareService _pakCompareService;
    private readonly PerformanceTracker _performanceTracker;
    private readonly IActivityLog _activityLog;
    private readonly string _dataFolder;
    private readonly string _outputPakPath;
    private readonly string _backupDirectory;

    public string Title => "Merge & Install";

    /// <summary>Each element is either a LibraryGroup (a real family) or a bare LibraryEntry (standalone) — same type-per-DataTemplate routing Library's own RootItems already uses.</summary>
    public ObservableCollection<object> LibraryRootItems { get; } = [];

    /// <summary>
    /// Mods queued for the next Rebuild — a mix of real EXMOD mods (field-merged against base game
    /// data) and opaque/prebuilt .pak entries (LibraryEntry.IsOpaquePak, no .EXMOD — unpacked and
    /// folded into the same merged pak instead, see RebuildService). Both live in one list since
    /// both end up in the same output; LoadQueuedPackagesAsync is what splits them back apart for
    /// RebuildService's own two-parameter signature.
    /// </summary>
    public ObservableCollection<LibraryEntry> Queue { get; } = [];

    [ObservableProperty]
    private LibraryEntry? _selectedLibraryItem;

    [ObservableProperty]
    private LibraryEntry? _selectedQueueEntry;

    [ObservableProperty]
    private bool _isRebuilding;

    [ObservableProperty]
    private int _rebuildProgressPercent;

    [ObservableProperty]
    private string? _rebuildStageText;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Whether this app's own pak is currently installed in the real game folder — refreshed at construction and after a successful install/remove, never live-polled. Drives InstallButtonLabel; deliberately coarse (not "has the queue changed since the last install", which would need a live diff every queue edit) — see the combined Install button's own doc comment for why.</summary>
    [ObservableProperty]
    private bool _hasExistingInstall;

    public string InstallButtonLabel => HasExistingInstall ? "Update install" : "Install";

    partial void OnHasExistingInstallChanged(bool value) => OnPropertyChanged(nameof(InstallButtonLabel));

    [ObservableProperty]
    private string? _installStatusMessage;

    public ObservableCollection<string> Warnings { get; } = [];

    // --- Advanced conflict picker ---
    // Null means "no manual picks" (every conflict resolves via the registry's own default rule).
    // Invalidated (reset to null) whenever Queue changes at all — a pick's own index only means
    // the mod it meant when it was made; any Add/Remove/Move/Clear could change which mod that
    // index now refers to, or whether the field is even still a conflict at all.
    private IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? _manualPicks;

    [ObservableProperty]
    private string? _conflictStatusMessage;

    /// <summary>Phase 10: a proactive conflict-count badge on the queue, recomputed in the background whenever Queue changes — Review conflicts still does the same computation synchronously for the authoritative picker flow; this is a best-effort preview, not load-bearing.</summary>
    [ObservableProperty]
    private int _conflictCount;

    public bool HasConflicts => ConflictCount > 0;

    partial void OnConflictCountChanged(int value) => OnPropertyChanged(nameof(HasConflicts));

    // --- Profile bar (6.3) ---
    public ObservableCollection<string> ProfileNames { get; } = [];

    [ObservableProperty]
    private string? _selectedProfileName;

    [ObservableProperty]
    private string _profileNameInput = "";

    [ObservableProperty]
    private string? _profileStatusMessage;

    // --- Export/Import patch (6.3) ---
    [ObservableProperty]
    private bool _isExportingPatch;

    [ObservableProperty]
    private string? _patchStatusMessage;

    // --- Gameplay-toggle merge options (6.4) ---
    public static IReadOnlyList<BoostLevel> BoostLevels { get; } = Enum.GetValues<BoostLevel>();

    public static IReadOnlyList<XpBoostLevel> XpBoostLevels { get; } = Enum.GetValues<XpBoostLevel>();

    public static IReadOnlyList<CraftCostReduction> CraftCostReductions { get; } = Enum.GetValues<CraftCostReduction>();

    [ObservableProperty]
    private BoostLevel _speedBoost;

    [ObservableProperty]
    private BoostLevel _playerBoost;

    [ObservableProperty]
    private XpBoostLevel _xpBoost;

    [ObservableProperty]
    private CraftCostReduction _craftCost;

    /// <summary>Free-text multiplier/percentage inputs, not fixed levels — classic IMM never documented an exact number for its own Stacks/Slots/Speed Crafting toggles, unlike the dropdowns above.</summary>
    [ObservableProperty]
    private string _stacksMultiplierInput = "";

    [ObservableProperty]
    private string _slotsMultiplierInput = "";

    [ObservableProperty]
    private string _speedCraftingPercentInput = "";

    [ObservableProperty]
    private bool _removeWeight;

    [ObservableProperty]
    private bool _unlimitedAmmo;

    [ObservableProperty]
    private bool _disableTemperatures;

    // --- Installed vs this list (6.6) ---
    /// <summary>"+ Name" (would be added), "- Name" (would be removed), "= Name" (unchanged) — a preview of what clicking Install would actually do, not run automatically since it needs a real read of the game folder.</summary>
    public ObservableCollection<string> ComparisonEntries { get; } = [];

    [ObservableProperty]
    private string? _comparisonStatusMessage;

    public MergeInstallViewModel(
        ILibraryRepository libraryRepository, IRebuildService rebuildService, IInstallService installService, IProfileStore profileStore,
        IPatchService patchService, IDaedalusCatalogClient daedalusClient, IJimk72CatalogClient jimk72Client,
        ISettingsService settingsService, IPakCompareService pakCompareService, PerformanceTracker performanceTracker, IActivityLog activityLog,
        string dataFolder, string outputPakPath, string backupDirectory)
    {
        _libraryRepository = libraryRepository;
        _rebuildService = rebuildService;
        _installService = installService;
        _profileStore = profileStore;
        _patchService = patchService;
        _daedalusClient = daedalusClient;
        _jimk72Client = jimk72Client;
        _settingsService = settingsService;
        _pakCompareService = pakCompareService;
        _performanceTracker = performanceTracker;
        _activityLog = activityLog;
        _dataFolder = dataFolder;
        _outputPakPath = outputPakPath;
        _backupDirectory = backupDirectory;

        ReloadLibrary();
        ReloadProfileNames();
        PrependBaselineMods();

        // Any queue change at all invalidates existing conflict picks — see _manualPicks' own
        // comment for why an Add/Remove/Move/Clear can silently change what a pick's index means.
        Queue.CollectionChanged += (_, _) =>
        {
            _manualPicks = null;
            ConflictStatusMessage = null;
            _ = RecomputeConflictCountAsync();
        };

        // This ViewModel is a DI singleton built once, so without this its own Library pane would
        // never learn about a mod imported/deleted from Library or Downloads until the app
        // restarts — this Send()s LibraryChangedMessage itself (after ImportPatchAsync/InstallAsync)
        // but, until now, never Registered to receive it from anywhere else.
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((MergeInstallViewModel)recipient).ReloadLibrary());

        // Without this, the Install/"Update install" label goes stale after the user changes the
        // Icarus Content path in Settings — the check below only otherwise runs at construction
        // and after this page's own install/remove actions.
        // The message parameter needs a real name: naming it "_" would make it shadow the discard,
        // so "_ = task" inside would try assigning the Task to the message parameter itself.
        WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (recipient, message) =>
        {
            _ = ((MergeInstallViewModel)recipient).RefreshHasExistingInstallAsync();
        });

        _ = RefreshHasExistingInstallAsync();
    }

    /// <summary>
    /// Checks for the real installed pak's own file directly, not GetInstalledStateAsync's parsed
    /// mod-name list — a gameplay-options-only install (an empty queue, per the spec's own "add
    /// merge options to game with no mods selected") still produces a real pak but lists zero mod
    /// names in the manifest, which would make a Count-based check wrongly say nothing's installed.
    /// </summary>
    private Task RefreshHasExistingInstallAsync() => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            HasExistingInstall = false;
            return;
        }

        try
        {
            var targetPakPath = Path.Combine(_settingsService.Current.IcarusContentPath!, "Paks", "mods", Path.GetFileName(_outputPakPath));
            HasExistingInstall = File.Exists(targetPakPath);
        }
        catch (Exception)
        {
            // Best-effort — the button label just stays whatever it already was.
        }
    });

    /// <summary>
    /// Every Library entry shows here, including opaque .pak entries (LibraryEntry.IsOpaquePak,
    /// marked with a small package icon in the row template) — matching Library's own page, which
    /// already shows both kinds together. An opaque entry has no .EXMOD to merge, but it can still
    /// be added to Queue: Rebuild unpacks it and folds its contents into the same merged pak
    /// instead of field-merging it (see RebuildService/LoadQueuedPackagesAsync).
    /// </summary>
    private void ReloadLibrary()
    {
        var groups = VariantGrouping.Group(_libraryRepository.GetAll())
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase);

        LibraryRootItems.Clear();
        foreach (var group in groups)
        {
            LibraryRootItems.Add(group.IsFamily ? group : group.Entries[0]);
        }
    }

    private void ReloadProfileNames()
    {
        ProfileNames.Clear();
        foreach (var name in _profileStore.ProfileNames)
        {
            ProfileNames.Add(name);
        }
    }

    private GameplayOptions BuildGameplayOptionsFromUi() => new()
    {
        SpeedBoost = SpeedBoost,
        PlayerBoost = PlayerBoost,
        XpBoost = XpBoost,
        CraftCost = CraftCost,
        StacksMultiplier = ParsePositiveDouble(StacksMultiplierInput),
        SlotsMultiplier = ParsePositiveDouble(SlotsMultiplierInput),
        SpeedCraftingReductionPercent = ParsePositiveDouble(SpeedCraftingPercentInput),
        RemoveWeight = RemoveWeight,
        UnlimitedAmmo = UnlimitedAmmo,
        DisableTemperatures = DisableTemperatures,
    };

    private void LoadGameplayOptionsIntoUi(GameplayOptions options)
    {
        SpeedBoost = options.SpeedBoost;
        PlayerBoost = options.PlayerBoost;
        XpBoost = options.XpBoost;
        CraftCost = options.CraftCost;
        StacksMultiplierInput = options.StacksMultiplier?.ToString() ?? "";
        SlotsMultiplierInput = options.SlotsMultiplier?.ToString() ?? "";
        SpeedCraftingPercentInput = options.SpeedCraftingReductionPercent?.ToString() ?? "";
        RemoveWeight = options.RemoveWeight;
        UnlimitedAmmo = options.UnlimitedAmmo;
        DisableTemperatures = options.DisableTemperatures;
    }

    private static double? ParsePositiveDouble(string text) =>
        double.TryParse(text, out var value) && value > 0 ? value : null;

    /// <summary>Selecting a profile replaces the current queue with that profile's own saved one — a profile *is* "a saved merge list" per the spec, not something merged alongside the current queue.</summary>
    partial void OnSelectedProfileNameChanged(string? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            var profile = _profileStore.Load(value);
            LoadGameplayOptionsIntoUi(profile.Options);

            Queue.Clear();
            var missingCount = 0;
            foreach (var folderName in profile.MergeQueueFolderNames)
            {
                var entry = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    Queue.Add(entry);
                }
                else
                {
                    missingCount++;
                }
            }

            PrependBaselineMods();

            ProfileStatusMessage = missingCount > 0
                ? $"Loaded '{value}' — {missingCount} mod(s) from this profile are no longer in your Library."
                : $"Loaded '{value}'.";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else: a corrupt/unreadable profile file shows a
            // status message rather than crashing the app out of a ComboBox selection change.
            ProfileStatusMessage = $"Couldn't load profile: {ex.Message}";
        }
    }

    /// <summary>Captures whatever's currently in the queue under a new profile name — lets a user build a queue first, then decide to save it, rather than New always starting empty.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        if (string.IsNullOrWhiteSpace(ProfileNameInput))
        {
            ProfileStatusMessage = "Type a profile name first.";
            return;
        }

        if (ProfileNames.Contains(ProfileNameInput, StringComparer.OrdinalIgnoreCase))
        {
            ProfileStatusMessage = $"A profile named '{ProfileNameInput}' already exists.";
            return;
        }

        try
        {
            var name = ProfileNameInput;
            _profileStore.Save(new Profile
            {
                Name = name,
                MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)],
                Options = BuildGameplayOptionsFromUi(),
            });
            ReloadProfileNames();
            SelectedProfileName = name;
            ProfileNameInput = "";
            ProfileStatusMessage = $"Created '{name}'.";
            _activityLog.Log($"Created profile '{name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't create profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedProfileName is not { } name)
        {
            ProfileStatusMessage = "Select or create a profile first.";
            return;
        }

        try
        {
            _profileStore.Save(new Profile
            {
                Name = name,
                MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)],
                Options = BuildGameplayOptionsFromUi(),
            });
            ProfileStatusMessage = $"Saved '{name}'.";
            _activityLog.Log($"Saved profile '{name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't save profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (SelectedProfileName is not { } oldName)
        {
            ProfileStatusMessage = "Select a profile first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProfileNameInput))
        {
            ProfileStatusMessage = "Type the new name first.";
            return;
        }

        if (ProfileNames.Contains(ProfileNameInput, StringComparer.OrdinalIgnoreCase))
        {
            ProfileStatusMessage = $"A profile named '{ProfileNameInput}' already exists.";
            return;
        }

        try
        {
            var newName = ProfileNameInput;
            _profileStore.Rename(oldName, newName);
            ReloadProfileNames();
            SelectedProfileName = newName;
            ProfileNameInput = "";
            ProfileStatusMessage = $"Renamed '{oldName}' to '{newName}'.";
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't rename profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfileName is not { } name)
        {
            ProfileStatusMessage = "Select a profile first.";
            return;
        }

        try
        {
            _profileStore.Delete(name);
            ReloadProfileNames();
            SelectedProfileName = null;
            ProfileStatusMessage = $"Deleted '{name}'.";
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't delete profile: {ex.Message}";
        }
    }

    /// <summary>
    /// "Bundles mods with no download URL, and mods you edited locally" per the spec — this app
    /// has no way to tell a locally-edited mod apart from a downloaded one yet (that needs the
    /// EXMOD editor, Phase 7), so the proxy used here is "not found in the community catalog by
    /// (Name, Author)": a friend can get anything catalog-listed from Downloads themselves, so
    /// only what they genuinely couldn't get elsewhere needs to travel inside the patch file
    /// itself. Exports whatever's currently in Queue (not a fresh reload of the saved profile),
    /// matching SaveProfile's own live-state semantics.
    /// </summary>
    [RelayCommand]
    private async Task ExportPatchAsync()
    {
        if (SelectedProfileName is not { } profileName)
        {
            PatchStatusMessage = "Select or save a profile first.";
            return;
        }

        if (Queue.Count == 0)
        {
            PatchStatusMessage = "Add at least one mod to the queue first.";
            return;
        }

        IsExportingPatch = true;
        PatchStatusMessage = "Checking which mods are in the community catalog…";

        try
        {
            // Isolated per-source, same as Downloads' own catalog refresh — a transient blip in
            // either source shouldn't fail the whole export; a mod from the source that failed
            // just gets treated as "not in catalog" (bundled) instead.
            var failedSources = new List<string>();
            var daedalusTask = CatalogSourceFetch.FetchAsync(_daedalusClient.FetchAsync, "Daedalus", failedSources);
            var jimk72Task = CatalogSourceFetch.FetchAsync(_jimk72Client.FetchAsync, "Jimk72", failedSources);
            await Task.WhenAll(daedalusTask, jimk72Task);
            var catalogKeys = daedalusTask.Result.Concat(jimk72Task.Result)
                .Select(e => CatalogKey.Normalize(e.Name, e.Author))
                .ToHashSet();

            var mods = new List<PatchModEntry>();
            var bundledContents = new Dictionary<string, ExmodPackageContents>();
            foreach (var entry in Queue)
            {
                var isInCatalog = catalogKeys.Contains(CatalogKey.Normalize(entry.Name, entry.Author));
                mods.Add(new PatchModEntry
                {
                    FolderName = entry.FolderName, Name = entry.Name, Author = entry.Author, Version = entry.Version,
                    Bundled = !isInCatalog,
                });
                if (!isInCatalog)
                {
                    bundledContents[entry.FolderName] = ExmodFolder.Read(_libraryRepository.GetFolderPath(entry.FolderName));
                }
            }

            var extension = bundledContents.Count == 0 ? "json" : "zip";
            var dialog = new SaveFileDialog
            {
                Title = "Export patch",
                FileName = $"ISL-Patch-{profileName}.{extension}",
                Filter = extension == "json" ? "IcarusStarlink patch (*.json)|*.json" : "IcarusStarlink patch (*.zip)|*.zip",
            };
            // ShowDialog() blocks until the user responds, so the "Checking…" message above would
            // otherwise sit on screen — stale-looking — for as long as they take to pick a location.
            PatchStatusMessage = "Choose where to save…";
            if (dialog.ShowDialog() != true)
            {
                PatchStatusMessage = null;
                return;
            }

            var manifest = new PatchManifest { ProfileName = profileName, Mods = mods, Options = BuildGameplayOptionsFromUi() };
            await _patchService.ExportAsync(manifest, bundledContents, dialog.FileName);

            PatchStatusMessage = bundledContents.Count > 0
                ? $"Exported '{profileName}' to '{dialog.FileName}' — {bundledContents.Count} mod(s) bundled, " +
                    $"{mods.Count - bundledContents.Count} referenced from the community catalog."
                : $"Exported '{profileName}' to '{dialog.FileName}' — every mod is in the community catalog, nothing bundled.";
            if (failedSources.Count > 0)
            {
                // Not a failure — a mod from the unreachable source was conservatively treated as
                // "not in catalog" and bundled instead, so the export is still complete and correct,
                // just possibly larger than it needed to be.
                PatchStatusMessage += $" ({string.Join(", ", failedSources)} couldn't be reached — affected mods were bundled rather than referenced.)";
            }
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExportingPatch = false;
        }
    }

    /// <summary>
    /// "Import + Sync reinstalls listed mods (force refresh; does not skip same version)" per the
    /// spec — a bundled mod always overwrites whatever's already in the Library under the same
    /// folder name, rather than skipping it as "already have it", since the whole point is syncing
    /// exactly what the patch carries. A referenced (non-bundled) mod is matched by (Name, Author)
    /// against the local Library; if it's missing, this reports it rather than failing the whole
    /// import — same SkipWithWarning philosophy as everywhere else the merge pipeline can hit a
    /// gap. ImportAsync itself doesn't need the network (unlike Export), but this still awaits it
    /// properly rather than blocking the UI thread on a zip read/extract that can take real time
    /// for a patch with bundled EXMODZ content.
    /// </summary>
    /// <summary>
    /// Loads a classic-IMM merge list (LastMergedMods.txt / IMM_Merged_Mod.txt — or this app's own
    /// ISL-Merged.txt, same format) and rebuilds the queue from it, in the list's own order (merge
    /// priority). REPLACES the current queue, matching classic IMM's own "Load Merge list"
    /// semantics — the file IS the intended list, not an addition to whatever happened to be
    /// queued. Names that don't match any Library entry land in the Warnings list rather than
    /// being silently dropped, so the user can resolve them (import the missing mod, or Rename/
    /// Link an existing one) and re-import.
    /// </summary>
    [RelayCommand]
    private void ImportImmModList()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import an IMM mod list",
            Filter = "Mod list (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var names = ModListText.ParseNames(File.ReadAllText(dialog.FileName));
            if (names.Count == 0)
            {
                StatusMessage = "That file has no mod names in it.";
                return;
            }

            var result = ModListMatcher.Match(names, _libraryRepository.GetAll());

            Queue.Clear();
            foreach (var entry in result.Matched)
            {
                Queue.Add(entry);
            }

            Warnings.Clear();
            foreach (var name in result.Unmatched)
            {
                Warnings.Add($"No Library match for '{name}' — import it first, then re-import this list.");
            }

            StatusMessage = result.Unmatched.Count == 0
                ? $"Queued all {result.Matched.Count} mod(s) from the list."
                : $"Queued {result.Matched.Count} of {names.Count} — {result.Unmatched.Count} not in your Library yet (see warnings below).";
            _activityLog.Log($"Imported IMM mod list — {result.Matched.Count} of {names.Count} matched.",
                result.Unmatched.Count == 0 ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
        }
        catch (Exception ex)
        {
            // Same UI boundary as ImportPatchAsync below — an unreadable/locked file shows a
            // status message rather than crashing the app.
            StatusMessage = $"Couldn't import the list: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportPatchAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a patch",
            Filter = "IcarusStarlink patch (*.json;*.zip)|*.json;*.zip",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var contents = await _patchService.ImportAsync(dialog.FileName);
            var resolvedFolderNames = new List<string>();
            var missing = new List<string>();

            foreach (var mod in contents.Manifest.Mods)
            {
                if (mod.Bundled)
                {
                    resolvedFolderNames.Add(ImportBundledMod(contents.BundledMods[mod.FolderName]));
                    continue;
                }

                var modKey = CatalogKey.Normalize(mod.Name, mod.Author);
                var existing = _libraryRepository.GetAll().FirstOrDefault(e => CatalogKey.Normalize(e.Name, e.Author) == modKey);
                if (existing is not null)
                {
                    resolvedFolderNames.Add(existing.FolderName);
                }
                else
                {
                    missing.Add($"{mod.Name} by {mod.Author}");
                }
            }

            _profileStore.Save(new Profile { Name = contents.Manifest.ProfileName, MergeQueueFolderNames = resolvedFolderNames, Options = contents.Manifest.Options });
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            ReloadLibrary();
            ReloadProfileNames();
            // Triggers OnSelectedProfileNameChanged, which populates Queue from the just-saved
            // profile and reports its own "N mod(s) no longer in your Library" count — reused as-is
            // rather than duplicating that population logic here.
            SelectedProfileName = contents.Manifest.ProfileName;

            PatchStatusMessage = missing.Count > 0
                ? $"Imported '{contents.Manifest.ProfileName}' — {missing.Count} mod(s) missing, get them from Downloads first: {string.Join(", ", missing)}."
                : $"Imported '{contents.Manifest.ProfileName}'.";
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private string ImportBundledMod(ExmodPackageContents contents)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.EXMODZ");
        try
        {
            ExmodzArchive.Write(tempPath, contents);

            var existing = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, contents.Package.FileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _libraryRepository.Delete(existing.FolderName);
            }

            return _libraryRepository.Import(tempPath).FolderName;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// The "standard/minimum" baseline list — classic IMM's own "create a standard list and then
    /// always merge that list with your current one" (ver 2.4.9). Saves the CURRENT queue as the
    /// baseline; an empty queue clears it. Baseline mods are then prepended (lowest merge
    /// priority, so a profile's own mods win conflicts) into every future queue load — visibly,
    /// as real queue rows, never as an invisible union at rebuild time.
    /// </summary>
    [RelayCommand]
    private void SetBaseline()
    {
        _settingsService.Current.BaselineModFolderNames = [.. Queue.Select(e => e.FolderName)];
        _settingsService.Save();
        StatusMessage = Queue.Count == 0
            ? "Baseline cleared — no mods are auto-added anymore."
            : $"Baseline set — these {Queue.Count} mod(s) will be auto-added to every profile's queue.";
        _activityLog.Log(Queue.Count == 0 ? "Cleared the baseline mod list." : $"Set baseline mod list ({Queue.Count} mod(s)).", ActivityEntryKind.Info);
    }

    /// <summary>
    /// Inserts baseline mods missing from the queue at its front, preserving the baseline's own
    /// stored order (index 0 = lowest merge priority, so the queue's own mods override a baseline
    /// mod on any conflicting field). A baseline mod no longer in the Library, or already covered
    /// by a queued merged pack, is skipped silently — the queue rows themselves are the visible
    /// truth of what will merge.
    /// </summary>
    private void PrependBaselineMods()
    {
        var insertAt = 0;
        foreach (var folderName in _settingsService.Current.BaselineModFolderNames)
        {
            if (Queue.Any(q => string.Equals(q.FolderName, folderName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var entry = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (entry is null || Queue.Any(q => q.MergedPackModNames?.Contains(entry.Name, StringComparer.OrdinalIgnoreCase) == true))
            {
                continue;
            }

            Queue.Insert(insertAt, entry);
            insertAt++;
        }
    }

    /// <summary>
    /// Spec: "skip mods already inside it; pack can replace individuals; new mods can sit beside
    /// the pack and merge on top" — the queue rules for a merged pack re-imported into Library.
    /// </summary>
    [RelayCommand]
    private void AddToQueue()
    {
        if (SelectedLibraryItem is not { } entry)
        {
            return;
        }

        if (Queue.Any(q => string.Equals(q.FolderName, entry.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"'{entry.Name}' is already in the queue.";
            return;
        }

        // Adding a mod that's already folded into a merged pack sitting in the queue is a no-op —
        // the pack already covers it, and merging it again would be redundant, not additive.
        var coveringPack = Queue.FirstOrDefault(q =>
            q.MergedPackModNames?.Contains(entry.Name, StringComparer.OrdinalIgnoreCase) == true);
        if (coveringPack is not null)
        {
            StatusMessage = $"'{entry.Name}' is already inside '{coveringPack.Name}', which is already queued.";
            return;
        }

        // Adding a merged pack replaces whichever of its own constituent mods are already queued
        // individually — the pack folds their contents in on its own, so keeping both would merge
        // the same mod's fields twice.
        if (entry.MergedPackModNames is { Count: > 0 } names)
        {
            var replaced = Queue.Where(q => names.Contains(q.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var individual in replaced)
            {
                Queue.Remove(individual);
            }

            if (replaced.Count > 0)
            {
                StatusMessage = $"'{entry.Name}' replaces {replaced.Count} individually-queued mod(s) it already contains.";
            }
        }

        Queue.Add(entry);
    }

    [RelayCommand]
    private void RemoveFromQueue()
    {
        if (SelectedQueueEntry is { } entry)
        {
            Queue.Remove(entry);
        }
    }

    [RelayCommand]
    private void ClearQueue() => Queue.Clear();

    [RelayCommand]
    private void MoveQueueEntryUp()
    {
        if (SelectedQueueEntry is not { } entry)
        {
            return;
        }

        var index = Queue.IndexOf(entry);
        if (index > 0)
        {
            Queue.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveQueueEntryDown()
    {
        if (SelectedQueueEntry is not { } entry)
        {
            return;
        }

        var index = Queue.IndexOf(entry);
        if (index >= 0 && index < Queue.Count - 1)
        {
            Queue.Move(index, index + 1);
        }
    }

    /// <summary>
    /// Big-plan item 8b: opens the pak-vs-pak comparison window (non-modal, like the editor's own
    /// research windows) — built for verifying this app's rebuilt pak against classic IMM's own
    /// installed merged pak during migration, but works for any two paks.
    /// </summary>
    [RelayCommand]
    private void ComparePaks()
    {
        var viewModel = new PakCompareViewModel(_pakCompareService, _settingsService, _outputPakPath);
        var window = new PakCompareWindow(viewModel) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>
    /// "Installed vs this list" — reads what this app can positively identify as currently
    /// installed (its own pak manifest in the real game folder) and diffs it against the current
    /// queue plus any attached prebuilt paks (both now folded into the one merged pak by Rebuild,
    /// so both are listed in the same manifest), so a user can see exactly what Install would
    /// change before clicking it. Gameplay options aren't part of this comparison — they're not a
    /// "mod" with a name to diff, they overwrite specific fields regardless of what's currently
    /// there. UE4SS mods aren't part of this either (Phase 8.5) — see Library's own UE4SS tab,
    /// which shows their real state directly.
    /// </summary>
    [RelayCommand]
    private async Task CompareToInstalledAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            ComparisonStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var installed = await _installService.GetInstalledStateAsync(_settingsService.Current.IcarusContentPath!);

            // For an opaque entry, the name RebuildService's own WriteManifest actually records is
            // Path.GetFileNameWithoutExtension of its resolved .pak file, not its (possibly Nexus-
            // enriched) display Name — the comparison below is a literal string match against what
            // the real manifest on disk says, so opaque entries need that same derivation here.
            var currentList = Queue.Select(entry =>
            {
                if (!entry.IsOpaquePak)
                {
                    return entry.Name;
                }

                var pakPath = Directory.GetFiles(_libraryRepository.GetFolderPath(entry.FolderName), "*.pak").FirstOrDefault();
                return pakPath is not null ? Path.GetFileNameWithoutExtension(pakPath) : entry.Name;
            }).ToList();
            var comparison = InstalledVsListComparer.Compare(installed.ModNames, currentList);

            ComparisonEntries.Clear();
            foreach (var entry in comparison.OrderBy(e => e.State).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var symbol = entry.State switch
                {
                    InstalledComparisonState.Added => "+",
                    InstalledComparisonState.Removed => "-",
                    _ => "=",
                };
                ComparisonEntries.Add($"{symbol}  {entry.Name}");
            }

            ComparisonStatusMessage = ComparisonEntries.Count == 0 ? "Nothing installed yet to compare against." : null;
        }
        catch (Exception ex)
        {
            ComparisonStatusMessage = $"Compare failed: {ex.Message}";
        }
    }

    /// <summary>
    /// The one visible button for turning the current queue into a real install — merges Rebuild
    /// and Install into a single click rather than requiring two, per direct user feedback ("why
    /// can't it just be an install and it merges and installs"). Internally still two distinct
    /// steps (RebuildAsync, a safe local-only pak build; InstallAsync, the one action in this whole
    /// app that writes into the real game folder, gated behind its own explicit Yes/No confirmation)
    /// — chaining them, rather than merging their bodies into one method, keeps each independently
    /// callable/testable and means the confirmation dialog still only appears right before the
    /// actual risky write, not before the safe rebuild step that precedes it. Skips the install
    /// step (and never shows its confirmation dialog at all) if the rebuild itself failed.
    /// </summary>
    [RelayCommand]
    private async Task RebuildAndInstallAsync()
    {
        await RebuildAsync();
        if (File.Exists(_outputPakPath))
        {
            await InstallAsync();
        }
    }

    // No [RelayCommand] — RebuildAndInstallAsync above is the only caller since the combined
    // Install button replaced the separate Rebuild/Install pair; the generated commands would be
    // dead bindings nothing in XAML references anymore.
    private async Task RebuildAsync()
    {
        var gameplayOptions = BuildGameplayOptionsFromUi();
        // Gameplay options can apply on their own, with an empty queue, per the spec ("the ability
        // to add merge options to game with no mods selected") — only block if there's genuinely
        // nothing to do at all. Queue can hold opaque pak entries too now (folded into the same
        // output by Rebuild, not field-merged), so a queue containing only those is still real work.
        if (Queue.Count == 0 && GameplayOptionsApplier.RequiredCurrentFiles(gameplayOptions).Count == 0)
        {
            StatusMessage = "Add at least one mod to the queue, or enable a gameplay option, first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_settingsService.Current.UnrealPakExePath))
        {
            StatusMessage = "Set UnrealPak.exe path in Settings first.";
            return;
        }

        IsRebuilding = true;
        StatusMessage = "Rebuilding…";
        RebuildProgressPercent = 0;
        RebuildStageText = null;
        Warnings.Clear();

        try
        {
            using var perfScope = _performanceTracker.Track("Rebuild");
            var (_, packages, prebuiltPakFilePaths) = await LoadQueuedPackagesAsync();

            // Progress<T>'s own callback marshals back through the SynchronizationContext captured
            // at construction time — safe to update these bound properties directly from it since
            // this constructor runs on the UI thread (a RelayCommand invoked from a button click),
            // even though RebuildAsync's own pipeline reports from a background Task.Run.
            var progress = new Progress<RebuildStageProgress>(p =>
            {
                RebuildStageText = p.Stage;
                RebuildProgressPercent = p.PercentComplete;
            });

            var result = await _rebuildService.RebuildAsync(
                packages, gameplayOptions, _dataFolder, _settingsService.Current.UnrealPakExePath!, _outputPakPath, prebuiltPakFilePaths, _manualPicks, progress);

            var prebuiltNote = prebuiltPakFilePaths.Count > 0 ? $", {prebuiltPakFilePaths.Count} prebuilt pak(s) folded in" : "";
            StatusMessage = $"Built '{result.OutputPakPath}' — {result.PackedFileCount} files packed, "
                + $"{result.MergedFileCount} data table(s) merged{prebuiltNote}.";
            foreach (var warning in result.Warnings)
            {
                Warnings.Add(warning);
            }

            var pickNote = _manualPicks is { Count: > 0 } picks ? $", {picks.Count} conflict(s) manually resolved" : "";
            _activityLog.Log($"Rebuilt pack with {Queue.Count} mod(s) — {result.PackedFileCount} files packed{pickNote}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else in this app: a missing base data file, a
            // UnrealPak failure, or a malformed queued mod should show a status message, not
            // crash the app.
            StatusMessage = $"Rebuild failed: {ex.Message}";
        }
        finally
        {
            IsRebuilding = false;
        }
    }

    /// <summary>
    /// Queue order = merge priority (index 0 lowest, matching MergeEngine's own convention) — read
    /// fresh from disk every time rather than caching, so an edit made outside the app (or via the
    /// EXMOD editor) is picked up. The snapshot itself is taken synchronously on the UI thread
    /// (cheap, no I/O) so a user edit to Queue (Add/Remove) mid-read can't race with the actual disk
    /// reads, which are the slow part and run off-thread on that fixed snapshot instead. Shared by
    /// RebuildAsync and ReviewConflictsAsync, which both need the exact same materialization.
    ///
    /// Queue can hold opaque pak entries (LibraryEntry.IsOpaquePak) alongside real EXMOD mods —
    /// split apart here since RebuildService needs them as two separate parameters. Entries stays
    /// EXMOD-only, kept 1:1 with Packages (FindQueueConflictsAsync zips them by index), so conflict
    /// checking naturally only ever considers mods that can actually have a field-level conflict.
    /// </summary>
    private async Task<(List<LibraryEntry> Entries, List<ExmodPackageContents> Packages, List<string> PrebuiltPakFilePaths)> LoadQueuedPackagesAsync()
    {
        var queueSnapshot = Queue.ToList();
        var exmodEntries = queueSnapshot.Where(e => !e.IsOpaquePak).ToList();
        var prebuiltEntries = queueSnapshot.Where(e => e.IsOpaquePak).ToList();

        var (packages, prebuiltPakFilePaths) = await Task.Run(() =>
        {
            var packages = exmodEntries
                .Select(entry => ExmodFolder.Read(_libraryRepository.GetFolderPath(entry.FolderName)))
                .ToList();

            // Each opaque pak folder holds exactly one .pak file (ImportPak's own write path) — a
            // folder whose .pak vanished since being queued (Library entry deleted/moved) is
            // silently skipped rather than failing the whole rebuild.
            var prebuiltPakFilePaths = prebuiltEntries
                .Select(entry => Directory.GetFiles(_libraryRepository.GetFolderPath(entry.FolderName), "*.pak").FirstOrDefault())
                .Where(path => path is not null)
                .Select(path => path!)
                .ToList();

            return (packages, prebuiltPakFilePaths);
        });

        return (exmodEntries, packages, prebuiltPakFilePaths);
    }

    /// <summary>Shared by ReviewConflictsAsync and the background badge computation — both need the exact same (queued packages) -&gt; (conflicts) pipeline.</summary>
    private async Task<(IReadOnlyList<FieldConflict> Conflicts, List<string> ModNames)> FindQueueConflictsAsync()
    {
        var (entries, packages, _) = await LoadQueuedPackagesAsync();
        return await Task.Run(() =>
        {
            var classifier = new DefaultSemanticClassifier();
            var orderedModChanges = packages.Select(p => ExmodFieldChangeMapper.ToFieldChanges(p.Package, classifier)).ToList();
            var names = entries.Select(e => e.Name).ToList();
            return ((IReadOnlyList<FieldConflict>)MergeEngine.FindConflicts(names, orderedModChanges), names);
        });
    }

    /// <summary>Best-effort background refresh of ConflictCount — a malformed queued mod here just leaves the badge at its last value; ReviewConflictsAsync/Rebuild surface the real error when the user actually acts.</summary>
    private async Task RecomputeConflictCountAsync()
    {
        if (Queue.Count == 0)
        {
            ConflictCount = 0;
            return;
        }

        try
        {
            var (conflicts, _) = await FindQueueConflictsAsync();
            ConflictCount = conflicts.Count;
        }
        catch (Exception)
        {
            ConflictCount = 0;
        }
    }

    /// <summary>
    /// The advanced conflict picker's own entry point — computes which fields two or more queued
    /// mods change differently and, if any, opens a window to let the user pick a winner per field
    /// (or leave it on the usual last-mod-wins default). Picks are stored in _manualPicks and used
    /// by the next Rebuild; they're invalidated the moment the queue changes at all, so this needs
    /// re-running after any Add/Remove/Move/Clear if the user wants to review again.
    /// </summary>
    [RelayCommand]
    private async Task ReviewConflictsAsync()
    {
        if (Queue.Count == 0)
        {
            ConflictStatusMessage = "Add mods to the queue first.";
            return;
        }

        try
        {
            var (conflicts, modNames) = await FindQueueConflictsAsync();
            ConflictCount = conflicts.Count;

            if (conflicts.Count == 0)
            {
                _manualPicks = null;
                ConflictStatusMessage = $"No conflicts among {modNames.Count} mod(s) — every changed field is touched by only one mod, or they all agree.";
                return;
            }

            _activityLog.Log($"{conflicts.Count} conflict(s) found among {modNames.Count} queued mod(s).", ActivityEntryKind.Warning);

            var pickerViewModel = new ConflictPickerViewModel(conflicts, _manualPicks);
            var window = new ConflictPickerWindow(pickerViewModel) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() == true)
            {
                var picks = window.ResultPicks!;
                _manualPicks = picks.Count > 0 ? picks : null;
                ConflictStatusMessage = picks.Count > 0
                    ? $"{conflicts.Count} conflict(s) found, {picks.Count} manually picked — Rebuild to apply."
                    : $"{conflicts.Count} conflict(s) found, all left on default (last mod wins).";
            }
        }
        catch (Exception ex)
        {
            // Same UI boundary as Rebuild itself — a malformed queued mod shouldn't crash the app.
            ConflictStatusMessage = $"Couldn't check for conflicts: {ex.Message}";
        }
    }

    /// <summary>
    /// The one method in this whole app that writes into the real game's Content\Paks\mods.
    /// No [RelayCommand] — reached only through RebuildAndInstallAsync's chain since the combined
    /// Install button replaced the separate pair; re-running that button after a locked-file
    /// failure still covers the spec's own "Copy built pack to game" retry case (the rebuild step
    /// just re-produces the same staged pak before this re-copies it).
    /// </summary>
    private async Task InstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        if (!File.Exists(_outputPakPath))
        {
            InstallStatusMessage = "Nothing staged yet — click Rebuild first.";
            return;
        }

        // Same explicit Yes/No gate as every other real-machine write this app makes (the nxm://
        // registry registration, the UE4SS loader install) — this one specifically overwrites the
        // user's actual installed mod pack, so it shouldn't ever be reachable via a single
        // accidental click.
        var confirmResult = MessageBox.Show(
            $"This copies the staged pak into '{_settingsService.Current.IcarusContentPath}\\Paks\\mods', overwriting whatever's currently installed there.\n\n" +
            "The existing pak is backed up first (last 5 kept).\n\nContinue?",
            "Install to Icarus", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        InstallStatusMessage = "Installing…";

        try
        {
            using var perfScope = _performanceTracker.Track("Install");
            var result = await _installService.InstallAsync(
                _outputPakPath, _settingsService.Current.IcarusContentPath!, _backupDirectory);

            // Replace, not accumulate: ImportPak would otherwise derive a fresh "_2"/"_3"-suffixed
            // folder name every time (its own collision-avoidance rule), leaving one stale Library
            // entry behind per install instead of one entry that stays current.
            var installedFolderName = Path.GetFileNameWithoutExtension(_outputPakPath);
            var existing = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, installedFolderName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _libraryRepository.Delete(existing.FolderName);
            }
            _libraryRepository.ImportPak(_outputPakPath);
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

            InstallStatusMessage = result.BackupPakPath is not null
                ? $"Installed to '{result.InstalledPakPath}'. Backed up the previous pak to '{result.BackupPakPath}'."
                : $"Installed to '{result.InstalledPakPath}'.";
            _activityLog.Log($"Installed pack to {_settingsService.Current.IcarusContentPath}\\Paks\\mods.", ActivityEntryKind.Success);
            HasExistingInstall = true;
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a locked target file (the game running), a
            // missing/wrong Content path, or a permissions issue should show a status message,
            // not crash the app. Retry is just clicking Install again once the cause is cleared.
            InstallStatusMessage = $"Install failed: {ex.Message}";
        }
    }

    /// <summary>
    /// A standalone uninstall — deletes the real installed pak from Content\Paks\mods without
    /// Installing anything in its place. Deliberately leaves the Library entry Install's own
    /// "Replace, not accumulate" step created untouched: removing a mod from the live game isn't
    /// the same request as deleting it from the local library, so this only ever touches the real
    /// game folder.
    /// </summary>
    [RelayCommand]
    private async Task RemoveFromGameAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        // Same explicit Yes/No gate InstallAsync itself already uses — this deletes a real file
        // from the user's actual game folder, so it shouldn't be reachable via a single accidental
        // click any more than overwriting it already isn't.
        var confirmResult = MessageBox.Show(
            $"This removes IcarusStarlink's installed pak from '{_settingsService.Current.IcarusContentPath}\\Paks\\mods'.\n\n" +
            "It's backed up first (last 5 kept), and your Library copy of this mod isn't touched — only the real game install.\n\nContinue?",
            "Remove from Icarus", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        InstallStatusMessage = "Removing…";

        try
        {
            var removed = await _installService.RemoveInstalledPakAsync(
                Path.GetFileName(_outputPakPath), _settingsService.Current.IcarusContentPath!, _backupDirectory);

            InstallStatusMessage = removed
                ? "Removed from Icarus — backed up first."
                : "Nothing installed by IcarusStarlink was there to remove.";
            if (removed)
            {
                _activityLog.Log($"Removed installed pack from {_settingsService.Current.IcarusContentPath}\\Paks\\mods.", ActivityEntryKind.Success);
                HasExistingInstall = false;
            }
        }
        catch (Exception ex)
        {
            InstallStatusMessage = $"Remove failed: {ex.Message}";
        }
    }
}
