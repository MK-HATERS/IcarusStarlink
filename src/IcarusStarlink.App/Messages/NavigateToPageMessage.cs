namespace IcarusStarlink.App.Messages;

/// <summary>
/// Asks the shell to switch pages, by nav item id ("downloads", "nexus", …). Sent by a page that
/// hands the user off to another one — e.g. Library's "Find in Database", which is only useful if
/// it actually takes you there. MainViewModel always exists, so unlike page ViewModels (built
/// lazily on first navigation) there is always a listener for this.
/// </summary>
public sealed record NavigateToPageMessage(string NavItemId);
