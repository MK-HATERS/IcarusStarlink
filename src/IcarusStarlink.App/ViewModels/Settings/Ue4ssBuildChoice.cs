using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Catalog.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

public enum Ue4ssBuildChoiceKind
{
    /// <summary>The build inside Starlink: no download.</summary>
    Bundled,

    /// <summary>GitHub's newest dev build ("experimental-latest"). Which build that is isn't known until GitHub is asked.</summary>
    NewestDev,

    /// <summary>One named build from GitHub's archive: an older dev build or the official 3.0.1.</summary>
    Archived,
}

/// <summary>One line of the UE4SS tab's "Which UE4SS to install" list.</summary>
public sealed partial class Ue4ssBuildChoice(Ue4ssBuildChoiceKind kind, string label, Ue4ssReleaseInfo? release = null) : ObservableObject
{
    public Ue4ssBuildChoiceKind Kind { get; } = kind;

    /// <summary>What the list shows, in plain words.</summary>
    [ObservableProperty]
    private string _label = label;

    /// <summary>The GitHub download; null for the built-in build, and for the newest dev build until GitHub has been asked.</summary>
    public Ue4ssReleaseInfo? Release { get; set; } = release;
}
