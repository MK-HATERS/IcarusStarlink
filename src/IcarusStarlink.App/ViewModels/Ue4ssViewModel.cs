using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class Ue4ssViewModel : ObservableObject
{
    public string Title => "UE4SS";

    public string PlaceholderMessage =>
        "Stage Lua/UE4SS mod folders so they install alongside your pak mods on Rebuild. Arrives in Phase 8.";
}
