namespace IcarusStarlink.App.Navigation;

/// <param name="Group">The heading it's listed under in the left column ("Find mods"); empty for none (Settings, Help).</param>
public sealed record NavItem(string Id, string Title, Type ViewModelType, string Group = "")
{
    /// <summary>The keys that open this page ("Ctrl+3"); empty when it has none.</summary>
    public string Shortcut { get; init; } = "";

    /// <summary>The folded menu's tooltip: the page's name and its shortcut, "Library (Ctrl+3)".</summary>
    public string TitleWithShortcut => Shortcut.Length == 0 ? Title : $"{Title} ({Shortcut})";

    /// <summary>What a screen reader says for the menu entry (the record's own text otherwise).</summary>
    public override string ToString() => Title;
}
