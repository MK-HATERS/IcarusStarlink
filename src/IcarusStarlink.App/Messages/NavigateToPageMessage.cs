namespace IcarusStarlink.App.Messages;

/// <summary>
/// Asks the shell to switch pages, by nav item id ("nexus", "library", …). Sent by a page that
/// hands the user off to another one — e.g. Library's "Search Nexus for this", which is only
/// useful if it actually takes you there. MainViewModel always exists, so unlike page ViewModels
/// (built lazily on first navigation) there is always a listener for this. "Find in Database"
/// used to be an example here too, back when Downloads was its own page — the IMM Database tab
/// now lives directly on Library, so that hand-off is just a same-page tab switch these days.
/// </summary>
public sealed record NavigateToPageMessage(string NavItemId);
