using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One downloaded file, MO2-style — never removed from Pending Downloads by Activate itself (only
/// Discard does that). IsInstalled/WasDeleted/ActionLabel are computed live against current
/// Library/UE4SS state rather than cached, since either can change independently while this page
/// is open (a delete elsewhere in the app); Refresh() re-raises change notifications for them.
/// One download can have produced SEVERAL items (a UE4SS mod plus its asset paks — see
/// PendingDownloadActivatedItem): "installed" means every one of them is still present.
/// </summary>
public sealed partial class PendingDownloadItemViewModel : ObservableObject
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IUe4ssModRepository _ue4ssModRepository;

    public int ModId { get; }
    public int FileId { get; }
    public string FileName { get; }
    public string LocalFilePath { get; }
    public DateTimeOffset DownloadedAtUtc { get; }

    /// <summary>The upload's own name, without Nexus's id/timestamp/hash tail or the extension — what a person recognizes the file as.</summary>
    public string DisplayName => NexusDownloadFileName.StripNexusSuffix(System.IO.Path.GetFileNameWithoutExtension(FileName));

    [ObservableProperty]
    private string? _activatedFolderName;

    [ObservableProperty]
    private PendingDownloadActivationKind? _activatedKind;

    [ObservableProperty]
    private IReadOnlyList<PendingDownloadActivatedItem> _activatedItems = [];

    [ObservableProperty]
    private IReadOnlyList<string> _requirements = [];

    public PendingDownloadItemViewModel(PendingDownloadEntry entry, ILibraryRepository libraryRepository, IUe4ssModRepository ue4ssModRepository)
    {
        _libraryRepository = libraryRepository;
        _ue4ssModRepository = ue4ssModRepository;
        ModId = entry.ModId;
        FileId = entry.FileId;
        FileName = entry.FileName;
        LocalFilePath = entry.LocalFilePath;
        DownloadedAtUtc = entry.DownloadedAtUtc;
        _activatedFolderName = entry.ActivatedFolderName;
        _activatedKind = entry.ActivatedKind;
        _activatedItems = entry.AllActivatedItems;
        _requirements = entry.Requirements;
    }

    public bool IsInstalled => ActivatedItems.Count > 0 && ActivatedItems.All(StillPresent);

    public bool WasDeleted => ActivatedItems.Count > 0 && !ActivatedItems.All(StillPresent);

    public string ActionLabel => IsInstalled ? "Reinstall" : "Activate";

    /// <summary>"Needs: ArcFission Library 1.2.0 or newer; UE4SS (experimental build)" — the README's own requirement lines, for the row.</summary>
    public string? RequirementsSummary => Requirements.Count == 0 ? null : $"Needs: {string.Join("; ", Requirements)}";

    /// <summary>The requirement NAMES (see ModReadmeRequirements.ExtractName) nothing staged or in the Library satisfies right now — the loader itself excluded. Drives the row's "Find on Nexus".</summary>
    public IReadOnlyList<string> MissingRequirementNames
    {
        get
        {
            if (Requirements.Count == 0)
            {
                return [];
            }

            var installed = _ue4ssModRepository.GetAll().Concat(_libraryRepository.GetAll().Select(e => e.Name)).ToList();
            return Requirements
                .Where(r => !ModReadmeRequirements.IsLoaderRequirement(r) && !ModReadmeRequirements.IsSatisfiedBy(r, installed))
                .Select(ModReadmeRequirements.ExtractName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool HasMissingRequirements => MissingRequirementNames.Count > 0;

    public string FindOnNexusLabel => MissingRequirementNames.Count switch
    {
        0 => "",
        1 => $"Find \"{MissingRequirementNames[0]}\" on Nexus",
        _ => $"Find \"{MissingRequirementNames[0]}\" on Nexus (+{MissingRequirementNames.Count - 1} more)",
    };

    public void SetActivated(IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<string> requirements)
    {
        ActivatedItems = items;
        Requirements = requirements;
        ActivatedFolderName = items.Count > 0 ? items[0].FolderName : null;
        ActivatedKind = items.Count > 0 ? items[0].Kind : null;
    }

    private bool StillPresent(PendingDownloadActivatedItem item) => item.Kind switch
    {
        PendingDownloadActivationKind.Library => _libraryRepository.GetAll()
            .Any(e => string.Equals(e.FolderName, item.FolderName, StringComparison.OrdinalIgnoreCase)),
        PendingDownloadActivationKind.Ue4ssMod => _ue4ssModRepository.GetAll()
            .Any(f => string.Equals(f, item.FolderName, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(WasDeleted));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(RequirementsSummary));
        OnPropertyChanged(nameof(MissingRequirementNames));
        OnPropertyChanged(nameof(HasMissingRequirements));
        OnPropertyChanged(nameof(FindOnNexusLabel));
    }

    partial void OnActivatedFolderNameChanged(string? value) => Refresh();
    partial void OnActivatedKindChanged(PendingDownloadActivationKind? value) => Refresh();
    partial void OnActivatedItemsChanged(IReadOnlyList<PendingDownloadActivatedItem> value) => Refresh();
    partial void OnRequirementsChanged(IReadOnlyList<string> value) => Refresh();
}
