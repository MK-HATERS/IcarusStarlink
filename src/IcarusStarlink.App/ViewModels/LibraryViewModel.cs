using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    public string Title => "Library";

    public string PlaceholderMessage =>
        "Import, search, and organize EXMODZ mods, with variant-family grouping. Arrives in Phase 3.";
}
