using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One downloaded file, MO2-style — never removed from Pending Downloads by Activate itself (only
/// Discard does that). IsInstalled/WasDeleted/ActionLabel are computed live against current
/// Library/UE4SS state rather than cached, since either can change independently while this page
/// is open (a delete elsewhere in the app); Refresh() re-raises change notifications for them,
/// called by DownloadsViewModel whenever LibraryChangedMessage arrives.
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

    [ObservableProperty]
    private string? _activatedFolderName;

    [ObservableProperty]
    private PendingDownloadActivationKind? _activatedKind;

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
    }

    /// <summary>True once this download has been activated AND that install is still present — drives the Activate button's "Reinstall" label.</summary>
    public bool IsInstalled => ActivatedFolderName is not null && StillPresent();

    /// <summary>True once activated but the folder it produced was since removed elsewhere — drives the "deleted from Library" indicator.</summary>
    public bool WasDeleted => ActivatedFolderName is not null && !StillPresent();

    public string ActionLabel => IsInstalled ? "Reinstall" : "Activate";

    private bool StillPresent() => ActivatedKind switch
    {
        PendingDownloadActivationKind.Library => _libraryRepository.GetAll()
            .Any(e => string.Equals(e.FolderName, ActivatedFolderName, StringComparison.OrdinalIgnoreCase)),
        PendingDownloadActivationKind.Ue4ssMod => _ue4ssModRepository.GetAll()
            .Any(f => string.Equals(f, ActivatedFolderName, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>Re-raises change notifications for the computed properties above without changing any stored value — call after Library/UE4SS state may have changed elsewhere.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(WasDeleted));
        OnPropertyChanged(nameof(ActionLabel));
    }

    partial void OnActivatedFolderNameChanged(string? value) => Refresh();
    partial void OnActivatedKindChanged(PendingDownloadActivationKind? value) => Refresh();
}
