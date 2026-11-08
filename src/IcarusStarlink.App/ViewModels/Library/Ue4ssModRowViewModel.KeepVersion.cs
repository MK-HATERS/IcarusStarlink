using System.ComponentModel;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.ViewModels;

/// <summary>"Keep this version" on a UE4SS mod (Ue4ssModMeta.KeptVersion): same switch as LibraryItemViewModel's.</summary>
public sealed partial class Ue4ssModRowViewModel
{
    /// <summary>What the switch skipped; null when it's off. Set by LibraryViewModel from the sidecar.</summary>
    public KeptVersion? KeptVersion
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVersionKept));
            OnPropertyChanged(nameof(HasUpdateAvailable));
        }
    }

    /// <summary>The switch is on: the context menu's tick.</summary>
    public bool IsVersionKept => KeptVersion is not null;

    /// <summary>The row's "Kept" mark: the switch is on and it is hiding (or there is no) update.</summary>
    public bool ShowsKeptMark => IsVersionKept && !HasUpdateAvailable;

    /// <summary>The update the switch hides, as the mark's tooltip says it.</summary>
    public string KeptMarkTooltip => LatestVersion is { Length: > 0 }
        ? $"You chose to keep this version, so Starlink doesn't offer {VersionLabel.For(KeptVersion?.SkippedVersion ?? LatestVersion)}. A newer update shows again. Right-click › Keep this version to turn it off."
        : "You chose to keep this version, so Starlink doesn't offer updates for it. Right-click › Keep this version to turn it off.";

    /// <summary>ShowsKeptMark follows HasUpdateAvailable, which many setters raise.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(HasUpdateAvailable))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowsKeptMark)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(KeptMarkTooltip)));
        }
    }
}
