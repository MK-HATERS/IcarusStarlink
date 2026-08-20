namespace IcarusStarlink.App.Messages;

/// <summary>
/// Broadcast whenever a mod is imported/deleted through the shared ILibraryRepository from
/// somewhere other than LibraryViewModel itself (e.g. Downloads' Download &amp; extract) — without
/// this, LibraryViewModel (a DI singleton, constructed once and never re-scanned unless something
/// tells it to) keeps showing whatever it had at construction time, so a mod downloaded from
/// another page silently doesn't appear in the Library tree until the user manually clicks its
/// own Refresh button.
/// </summary>
public sealed record LibraryChangedMessage;
