namespace IcarusStarlink.App.Messages;

/// <summary>
/// Asks the shell to switch pages, by nav item id ("library", "downloads", …); "nexus" and
/// "imm-database" open the Database page on that tab. Sent by a page that hands the user off to
/// another one, e.g. Library's "Search Nexus for this". MainViewModel always exists, so there is
/// always a listener, even before a page's view model is built.
/// </summary>
public sealed record NavigateToPageMessage(string NavItemId);
