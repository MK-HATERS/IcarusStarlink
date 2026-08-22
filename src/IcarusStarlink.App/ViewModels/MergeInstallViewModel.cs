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
using IcarusStarlink.Core.InstallComparison;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Diffing;
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
    private readonly PerformanceTracker _performanceTracker;
    private readonly string _dataFolder;
    private readonly string _outputPakPath;
    private readonly string _backupDirectory;

    public string Title => "Merge & Install";

    /// <summary>Each element is either a LibraryGroup (a real family) or a bare LibraryEntry (standalone) — same type-per-DataTemplate routing Library's own RootItems already uses.</summary>
    public ObservableCollection<object> LibraryRootItems { get; } = [];

    public ObservableCollection<LibraryEntry> Queue { get; } = [];

    [ObservableProperty]
    private LibraryEntry? _selectedLibraryItem;

    [ObservableProperty]
    private LibraryEntry? _selectedQueueEntry;

    [ObservableProperty]
    private bool _isRebuilding;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isInstalling;

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

    public static IReadOnlyList<CraftCostReduction> CraftCostReductions { get; } = Enum.GetValues<CraftCostReduction>();

    [ObservableProperty]
    private BoostLevel _speedBoost;

    [ObservableProperty]
    private BoostLevel _playerBoost;

    [ObservableProperty]
    private BoostLevel _xpBoost;

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
        ISettingsService settingsService, PerformanceTracker performanceTracker, string dataFolder, string outputPakPath, string backupDirectory)
    {
        _libraryRepository = libraryRepository;
        _rebuildService = rebuildService;
        _installService = installService;
        _profileStore = profileStore;
        _patchService = patchService;
        _daedalusClient = daedalusClient;
        _jimk72Client = jimk72Client;
        _settingsService = settingsService;
        _performanceTracker = performanceTracker;
        _dataFolder = dataFolder;
        _outputPakPath = outputPakPath;
        _backupDirectory = backupDirectory;

        ReloadLibrary();
        ReloadProfileNames();

        // Any queue change at all invalidates existing conflict picks — see _manualPicks' own
        // comment for why an Add/Remove/Move/Clear can silently change what a pick's index means.
        Queue.CollectionChanged += (_, _) =>
        {
            _manualPicks = null;
            ConflictStatusMessage = null;
        };

        // This ViewModel is a DI singleton built once, so without this its own Library pane would
        // never learn about a mod imported/deleted from Library or Downloads until the app
        // restarts — this Send()s LibraryChangedMessage itself (after ImportPatchAsync/InstallAsync)
        // but, until now, never Registered to receive it from anywhere else.
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((MergeInstallViewModel)recipient).ReloadLibrary());
    }

    /// <summary>
    /// Opaque .pak entries (LibraryEntry.IsOpaquePak) are excluded here — they have no .EXMOD to
    /// merge in. The spec treats a prebuilt pak as a sidecar copied alongside the merged pack on
    /// install instead (6.2's job), not something Rebuild folds in.
    /// </summary>
    private void ReloadLibrary()
    {
        var groups = VariantGrouping.Group(_libraryRepository.GetAll().Where(e => !e.IsOpaquePak))
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
            _profileStore.Save(new Profile { Name = name, MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)], Options = BuildGameplayOptionsFromUi() });
            ReloadProfileNames();
            SelectedProfileName = name;
            ProfileNameInput = "";
            ProfileStatusMessage = $"Created '{name}'.";
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
            _profileStore.Save(new Profile { Name = name, MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)], Options = BuildGameplayOptionsFromUi() });
            ProfileStatusMessage = $"Saved '{name}'.";
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
    /// "Installed vs this list" — reads what this app can positively identify as currently
    /// installed (its own pak manifest in the real game folder) and diffs it against the current
    /// queue, so a user can see exactly what Install would change before clicking it. Gameplay
    /// options aren't part of this comparison — they're not a "mod" with a name to diff, they
    /// overwrite specific fields regardless of what's currently there. UE4SS mods aren't part of
    /// this either (Phase 8.5) — see Library's own UE4SS tab, which shows their real state directly.
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
            var comparison = InstalledVsListComparer.Compare(installed.ModNames, [.. Queue.Select(e => e.Name)]);

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

    [RelayCommand]
    private async Task RebuildAsync()
    {
        var gameplayOptions = BuildGameplayOptionsFromUi();
        // Gameplay options can apply on their own, with an empty queue, per the spec ("the ability
        // to add merge options to game with no mods selected") — only block if there's genuinely
        // nothing to do at all.
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
        Warnings.Clear();

        try
        {
            using var perfScope = _performanceTracker.Track("Rebuild");
            var (_, packages) = await LoadQueuedPackagesAsync();

            var result = await _rebuildService.RebuildAsync(
                packages, gameplayOptions, _dataFolder, _settingsService.Current.UnrealPakExePath!, _outputPakPath, _manualPicks);

            StatusMessage = $"Built '{result.OutputPakPath}' — {result.PackedFileCount} files packed, "
                + $"{result.MergedFileCount} data table(s) merged.";
            foreach (var warning in result.Warnings)
            {
                Warnings.Add(warning);
            }
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
    /// </summary>
    private async Task<(List<LibraryEntry> Entries, List<ExmodPackageContents> Packages)> LoadQueuedPackagesAsync()
    {
        var entriesSnapshot = Queue.ToList();
        var packages = await Task.Run(() => entriesSnapshot
            .Select(entry => ExmodFolder.Read(_libraryRepository.GetFolderPath(entry.FolderName)))
            .ToList());
        return (entriesSnapshot, packages);
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
            var (entries, packages) = await LoadQueuedPackagesAsync();
            var (conflicts, modNames) = await Task.Run(() =>
            {
                var classifier = new DefaultSemanticClassifier();
                var orderedModChanges = packages.Select(p => ExmodFieldChangeMapper.ToFieldChanges(p.Package, classifier)).ToList();
                var names = entries.Select(e => e.Name).ToList();
                return (MergeEngine.FindConflicts(names, orderedModChanges), names);
            });

            if (conflicts.Count == 0)
            {
                _manualPicks = null;
                ConflictStatusMessage = $"No conflicts among {modNames.Count} mod(s) — every changed field is touched by only one mod, or they all agree.";
                return;
            }

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
    /// The one command in this whole app that writes into the real game's Content\Paks\mods.
    /// Also serves as the spec's own "Copy built pack to game" retry action — clicking it again
    /// after a locked-file failure just re-copies the same already-staged pak, no rebuild needed.
    /// </summary>
    [RelayCommand]
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

        IsInstalling = true;
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
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a locked target file (the game running), a
            // missing/wrong Content path, or a permissions issue should show a status message,
            // not crash the app. Retry is just clicking Install again once the cause is cleared.
            InstallStatusMessage = $"Install failed: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
        }
    }

}
