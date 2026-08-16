using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class MergeInstallViewModel : ObservableObject
{
    public string Title => "Merge & Install";

    public string PlaceholderMessage =>
        "Build an ordered merge queue, apply gameplay toggles, and rebuild the installed pak. Arrives in Phase 6.";
}
