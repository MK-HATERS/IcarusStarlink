using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One downloaded file, MO2-style — never removed from Pending Downloads by Activate itself (only
/// Discard does that). IsInstalled/WasDeleted/ActionLabel are computed live against current
/// Library/UE4SS state rather than cached, since either can change independently while this page
/// is open (a delete elsewhere in the app); Refresh() re-raises change notifications for them.
/// What's installed is read once per Refresh (an InstalledModsSnapshot, shared by every row when the
/// page refreshes them together) — each property used to re-read the Library and the game's Mods
/// folder on its own.
/// One download can have produced SEVERAL items (a UE4SS mod plus its asset paks — see
/// PendingDownloadActivatedItem): "installed" means every one of them is still present.
/// </summary>
public sealed partial class PendingDownloadItemViewModel : ObservableObject
{
    private readonly ILibraryRepository _libraryRepository;

    // An ENABLED UE4SS mod lives in the game's own Mods folder, not in staging — GetAll alone said
    // every enabled Lua half was gone, so a fully installed download read "Removed from Library".
    private readonly Ue4ssInstalledNames _ue4ssNames;

    private InstalledModsSnapshot? _snapshot;

    private InstalledModsSnapshot Installed => _snapshot ??= InstalledModsSnapshot.Take(_libraryRepository, _ue4ssNames);

    public int ModId { get; }
    public int FileId { get; }
    public string FileName { get; }
    public string LocalFilePath { get; }
    public DateTimeOffset DownloadedAtUtc { get; }

    /// <summary>The mod's name as its source listed it (a catalog entry's Name), else the upload's own file name without Nexus's id/timestamp/hash tail or the extension — what a person recognizes the file as.</summary>
    public string DisplayName => _displayName ?? NexusDownloadFileName.StripNexusSuffix(System.IO.Path.GetFileNameWithoutExtension(FileName), IsCatalog ? null : ModId);

    private readonly string? _displayName;

    /// <summary>"Nexus" or "Database" — the Downloads page's Source column.</summary>
    public string Source { get; }

    public string? CatalogEntryId { get; }

    public bool IsCatalog { get; }

    /// <summary>The author the source listed (a catalog entry's), or null — see PendingDownloadEntry.Author.</summary>
    public string? Author { get; }

    [ObservableProperty]
    private IReadOnlyList<PendingDownloadActivatedItem> _activatedItems = [];

    [ObservableProperty]
    private IReadOnlyList<string> _requirements = [];

    public PendingDownloadItemViewModel(PendingDownloadEntry entry, ILibraryRepository libraryRepository, IUe4ssModRepository ue4ssModRepository, Ue4ssInstalledNames? ue4ssNames = null)
    {
        _libraryRepository = libraryRepository;
        _ue4ssNames = ue4ssNames ?? new Ue4ssInstalledNames(ue4ssModRepository, settings: null);
        ModId = entry.ModId;
        FileId = entry.FileId;
        FileName = entry.FileName;
        LocalFilePath = entry.LocalFilePath;
        DownloadedAtUtc = entry.DownloadedAtUtc;
        _displayName = entry.DisplayName;
        IsCatalog = entry.IsCatalog;
        Source = IsCatalog ? PendingDownloadEntry.DatabaseSource : PendingDownloadEntry.NexusSource;
        CatalogEntryId = entry.CatalogEntryId;
        Author = entry.Author;
        _activatedItems = entry.AllActivatedItems;
        _requirements = entry.Requirements;
    }

    /// <summary>Whether this row's download is the same file as <paramref name="entry"/> — a reload keeps the row (a running Activate holds it) rather than building a new one.</summary>
    public bool IsSameDownload(PendingDownloadEntry entry) =>
        entry.ModId == ModId && entry.FileId == FileId && entry.FileName == FileName && entry.LocalFilePath == LocalFilePath && entry.DownloadedAtUtc == DownloadedAtUtc;

    public bool IsInstalled => ActivatedItems.Count > 0 && ActivatedItems.All(StillPresent);

    public bool WasDeleted => ActivatedItems.Count > 0 && !ActivatedItems.All(StillPresent);

    /// <summary>"Reinstall" for anything that has been installed before (still there, or removed since) — "Activate" only for a download never imported.</summary>
    public string ActionLabel => ActivatedItems.Count > 0 ? "Reinstall" : "Activate";

    /// <summary>"Needs: ArcFission Library 1.2.0 or newer; UE4SS (experimental build)" — the README's own requirement lines, for the row.</summary>
    public string? RequirementsSummary
    {
        get
        {
            var lines = Requirements.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
            return lines.Count == 0 ? null : $"Needs: {string.Join("; ", lines)}";
        }
    }

    /// <summary>The requirement NAMES (see ModReadmeRequirements.ExtractName) nothing staged or in the Library satisfies right now — the loader itself excluded. Drives the row's "Find on Nexus".</summary>
    public IReadOnlyList<string> MissingRequirementNames
    {
        get
        {
            if (Requirements.Count == 0)
            {
                return [];
            }

            var installed = Installed.ModNames;
            return Requirements
                .Where(r => !ModReadmeRequirements.NeedsNoDownload(r) && !ModReadmeRequirements.IsSatisfiedBy(r, installed))
                .Select(ModReadmeRequirements.ExtractName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool HasMissingRequirements => MissingRequirementNames.Count > 0;

    /// <summary>The "Get X" button only makes sense for a download that is (still) installed — offering to fetch a dependency for a mod the user just deleted read as "find X to delete it too" in a real report.</summary>
    public bool CanGetRequirement => HasMissingRequirements && !WasDeleted;

    public string GetRequirementLabel => MissingRequirementNames.Count switch
    {
        0 => "",
        1 => $"Get '{MissingRequirementNames[0]}'",
        _ => $"Get '{MissingRequirementNames[0]}' (+{MissingRequirementNames.Count - 1} more)",
    };

    /// <summary>"UE4SS mod + 2 paks", "1 mod", "" — what this download turned into, in plain words.</summary>
    public string PartsSummary
    {
        get
        {
            if (ActivatedItems.Count == 0)
            {
                return "";
            }

            var lua = ActivatedItems.Count(i => i.Kind == PendingDownloadActivationKind.Ue4ssMod);
            var library = ActivatedItems.Count(i => i.Kind == PendingDownloadActivationKind.Library);
            var parts = new List<string>();
            if (library > 0)
            {
                parts.Add(library == 1 ? "Library mod" : $"{library} Library mods");
            }

            if (lua > 0)
            {
                parts.Add(lua == 1 ? "UE4SS mod" : $"{lua} UE4SS mods");
            }

            return string.Join(" + ", parts);
        }
    }

    /// <summary>Installed / Removed from Library / Not installed — one phrase for the row, no inference needed.</summary>
    public string StateLabel => IsInstalled ? "Installed" : WasDeleted ? "Removed from Library" : "Not installed";

    public void SetActivated(IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<string> requirements)
    {
        ActivatedItems = items;
        Requirements = requirements;
    }

    private bool StillPresent(PendingDownloadActivatedItem item) => item.Kind switch
    {
        PendingDownloadActivationKind.Library => Installed.LibraryFolders.Contains(item.FolderName),
        PendingDownloadActivationKind.Ue4ssMod => Installed.Ue4ssNames.Contains(item.FolderName),
        _ => false,
    };

    /// <summary>Re-reads what's installed (or takes <paramref name="snapshot"/>, read once for many rows) and re-raises every computed property.</summary>
    public void Refresh(InstalledModsSnapshot? snapshot = null)
    {
        _snapshot = snapshot ?? InstalledModsSnapshot.Take(_libraryRepository, _ue4ssNames);
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(WasDeleted));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(RequirementsSummary));
        OnPropertyChanged(nameof(MissingRequirementNames));
        OnPropertyChanged(nameof(HasMissingRequirements));
        OnPropertyChanged(nameof(CanGetRequirement));
        OnPropertyChanged(nameof(GetRequirementLabel));
        OnPropertyChanged(nameof(PartsSummary));
        OnPropertyChanged(nameof(StateLabel));
    }

    partial void OnActivatedItemsChanged(IReadOnlyList<PendingDownloadActivatedItem> value) => Refresh();
    partial void OnRequirementsChanged(IReadOnlyList<string> value) => Refresh();
}

/// <summary>
/// What's installed at one moment: Library folders and names, and UE4SS names staged or in the
/// game. Each part is read on first use (a list of Nexus-only downloads never needs the game's
/// Mods folder), then kept for every row refreshed with this snapshot.
/// </summary>
public sealed class InstalledModsSnapshot
{
    private readonly Lazy<IReadOnlyList<LibraryEntry>> _libraryEntries;
    private readonly Lazy<IReadOnlyList<string>> _ue4ssNames;
    private readonly Lazy<IReadOnlySet<string>> _libraryFolders;
    private readonly Lazy<IReadOnlySet<string>> _ue4ssNameSet;
    private readonly Lazy<IReadOnlyList<string>> _modNames;

    private InstalledModsSnapshot(ILibraryRepository library, Ue4ssInstalledNames ue4ssNames)
    {
        _libraryEntries = new(() => library.GetAll());
        _ue4ssNames = new(ue4ssNames.All);
        _libraryFolders = new(() => _libraryEntries.Value.Select(e => e.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase));
        _ue4ssNameSet = new(() => _ue4ssNames.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));
        _modNames = new(() => [.. _ue4ssNames.Value, .. _libraryEntries.Value.Select(e => e.Name)]);
    }

    public IReadOnlySet<string> LibraryFolders => _libraryFolders.Value;

    public IReadOnlySet<string> Ue4ssNames => _ue4ssNameSet.Value;

    /// <summary>UE4SS names plus Library mod names — what a README requirement is matched against.</summary>
    public IReadOnlyList<string> ModNames => _modNames.Value;

    public static InstalledModsSnapshot Take(ILibraryRepository library, Ue4ssInstalledNames ue4ssNames) => new(library, ue4ssNames);
}
