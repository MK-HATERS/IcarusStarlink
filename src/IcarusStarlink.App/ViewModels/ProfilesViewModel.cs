using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class ProfilesViewModel : ObservableObject
{
    public string Title => "Profiles";

    public string PlaceholderMessage =>
        "Save merge-queue profiles and export shareable patches for friends or a server. Arrives in Phase 8.";
}
