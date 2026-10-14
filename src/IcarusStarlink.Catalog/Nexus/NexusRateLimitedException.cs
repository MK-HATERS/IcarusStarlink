namespace IcarusStarlink.Catalog.Nexus;

/// <summary>
/// Nexus answered 429 (hourly/daily API budget spent). A distinct type (an audit finding): it used
/// to be a plain InvalidOperationException, the same type the download endpoint's "not Premium"
/// rejection throws, so a rate-limited Premium user was sent to the website with "if your account
/// isn't Premium, click Mod Manager Download" — which fails the same way. Still derives from
/// InvalidOperationException so every existing catch keeps working.
/// </summary>
public sealed class NexusRateLimitedException(string message) : InvalidOperationException(message);
