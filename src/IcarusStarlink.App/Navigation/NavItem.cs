namespace IcarusStarlink.App.Navigation;

/// <param name="Group">The heading it's listed under in the left column ("Find mods"); empty for none (Settings, Help).</param>
public sealed record NavItem(string Id, string Title, Type ViewModelType, string Group = "");
