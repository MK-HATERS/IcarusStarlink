namespace IcarusStarlink.Catalog.Nexus;

/// <summary>
/// Nexus refused a download link (401/403 from download_link): a non-Premium account with no
/// key/expires from the website's Mod Manager Download button, or an expired one. A distinct type
/// so only this sends the user to the mod page with the Premium advice — every other
/// InvalidOperationException used to as well (a review on 2026-10-23). Still derives from
/// InvalidOperationException so every existing catch keeps working.
/// </summary>
public sealed class NexusDownloadRejectedException(string message) : InvalidOperationException(message);
