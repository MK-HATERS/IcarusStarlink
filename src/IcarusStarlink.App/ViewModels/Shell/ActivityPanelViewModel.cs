using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Phase 10: the persistent activity drawer, toggled via the header's bell button — see IActivityLog's own doc comment for why this exists.</summary>
public sealed partial class ActivityPanelViewModel : ObservableObject
{
    public ObservableCollection<ActivityEntry> Entries { get; }

    public bool HasEntries => Entries.Count > 0;
    public bool IsEmpty => !HasEntries;

    [ObservableProperty]
    private bool _isOpen;

    public ActivityPanelViewModel(IActivityLog activityLog)
    {
        Entries = activityLog.Entries;
        Entries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;
}
