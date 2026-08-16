using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    public string Title => "Diagnostics";

    public string PlaceholderMessage =>
        "Export a sanitized diagnostics bundle (logs + settings, no secrets) for bug reports. Arrives in Phase 9.";
}
