using System.ComponentModel;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.ViewModels;

/// <summary>"Keep this version": the switch that stops the Update badge and Update all for this mod (LibraryEntry.KeptVersion).</summary>
public sealed partial class LibraryItemViewModel
{
    /// <summary>What the switch skipped; null when it's off.</summary>
    public KeptVersion? KeptVersion { get; private set; }

    /// <summary>The switch is on: the context menu's tick.</summary>
    public bool IsVersionKept => KeptVersion is not null;

    /// <summary>The row's "Kept" mark: the switch is on and it is hiding (or there is no) update.</summary>
    public bool ShowsKeptMark => IsVersionKept && !HasUpdateAvailable;

    /// <summary>The update the switch hides, as the mark's tooltip says it.</summary>
    public string KeptMarkTooltip => LatestVersion is { Length: > 0 }
        ? $"You chose to keep this version, so Starlink doesn't offer {VersionLabel.For(KeptVersion?.SkippedVersion ?? LatestVersion)}. A newer update shows again. Right-click › Keep this version to turn it off."
        : "You chose to keep this version, so Starlink doesn't offer updates for it. Right-click › Keep this version to turn it off.";

    /// <summary>The update a check found, before the switch is applied: what turning the switch on skips.</summary>
    internal bool HasUpdateIgnoringKept => HasSource && !IsAheadOfNexus && (NexusFileId is { } installedFile && LatestNexusFileId is { } newestFile
        ? newestFile != installedFile && !UpdateVersionComparer.AreEquivalent(LatestVersion, Version)
        : UpdateVersionComparer.HasUpdate(LatestVersion, Version, NexusVersion));

    /// <summary>Sets the switch after the repository saved it (or from a rescan).</summary>
    internal void SetKeptVersion(KeptVersion? kept)
    {
        KeptVersion = kept;
        OnPropertyChanged(nameof(KeptVersion));
        OnPropertyChanged(nameof(IsVersionKept));
        OnPropertyChanged(nameof(KeptMarkTooltip));
        OnPropertyChanged(nameof(HasUpdateAvailable));
    }

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
