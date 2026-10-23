namespace IcarusStarlink.Catalog.AppUpdate;

/// <summary>What the update check offers, and why — the wording differs for each.</summary>
public enum AppUpdateOffer
{
    /// <summary>Up to date (or nothing could be read).</summary>
    None,

    /// <summary>A newer release on the chosen channel.</summary>
    Newer,

    /// <summary>Running a beta, and a newer stable release is out ("Stable v2.10.0 is ready").</summary>
    StableReady,

    /// <summary>Running a beta with the Stable channel chosen: the newest stable release, even when its number is lower.</summary>
    SwitchBackToStable,
}

/// <summary>
/// The update channels (user's design, 2026-10-23). Stable offers finished releases only (and,
/// while running a beta, the newest one to switch back to). Beta offers whichever is newer of the
/// newest beta and the newest stable release, so a beta tester also gets the final release.
/// </summary>
public static class AppUpdateChoice
{
    /// <summary>The release the channel offers, before comparing it with what's installed.</summary>
    public static AppUpdateRelease? Pick(AppUpdateRelease? stable, AppUpdateRelease? beta, bool betaChannel)
    {
        if (!betaChannel || beta is null)
        {
            return stable;
        }

        if (stable is null)
        {
            return beta;
        }

        return AppReleaseVersion.TryParse(beta.Version) is { } betaVersion
            && AppReleaseVersion.TryParse(stable.Version) is { } stableVersion
            && betaVersion > stableVersion ? beta : stable;
    }

    public static AppUpdateOffer Classify(string installedVersion, AppUpdateRelease? offered, bool betaChannel)
    {
        if (offered is null
            || AppReleaseVersion.TryParse(offered.Version) is not { } latest
            || AppReleaseVersion.TryParse(installedVersion) is not { } installed)
        {
            return AppUpdateOffer.None;
        }

        if (latest > installed)
        {
            return installed.IsPrerelease && !latest.IsPrerelease ? AppUpdateOffer.StableReady : AppUpdateOffer.Newer;
        }

        return !betaChannel && installed.IsPrerelease && !latest.IsPrerelease && latest.CompareTo(installed) != 0
            ? AppUpdateOffer.SwitchBackToStable
            : AppUpdateOffer.None;
    }
}
