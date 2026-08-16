using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class DownloadsViewModel : ObservableObject
{
    public string Title => "Downloads";

    public string PlaceholderMessage =>
        "Browse the live community mod catalog (Project Daedalus + Jimk72's list) and import mods directly. Arrives in Phase 4.";
}
