namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// Reads the command line Windows runs for an nxm:// link (HKCU\Software\Classes\nxm\shell\open\
/// command, e.g. <c>"C:\Tools\Vortex.exe" -d "%1"</c>). Windows holds one nxm:// handler for every
/// game, so another mod manager registering itself took Icarus links away from Starlink (user
/// report 2026-10-23: they kept re-checking Settings). Starlink now remembers the handler it
/// replaced and hands it links for other games; these are the string rules behind that.
/// </summary>
public static class NxmHandlerCommand
{
    public const string IcarusDomain = "icarus";

    private const string StarlinkExeName = "IcarusStarlink.App.exe";

    /// <summary>The exe a handler command starts: the quoted first part, else everything up to the first space.</summary>
    public static string? ExePathOf(string? command)
    {
        var text = command?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    /// <summary>Any Starlink install, not only this one: two Starlinks forwarding to each other would pass a link back and forth forever.</summary>
    public static bool IsStarlink(string? command) =>
        string.Equals(Path.GetFileName(ExePathOf(command)), StarlinkExeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The exe and its arguments with %1 swapped for the link, ready for Process.Start; null when the command has no exe.</summary>
    public static (string FileName, string Arguments)? LaunchFor(string? command, string nxmUrl)
    {
        var exe = ExePathOf(command);
        if (exe is null)
        {
            return null;
        }

        var text = command!.Trim();
        var rest = text[0] == '"' ? text[(exe.Length + 2)..] : text[exe.Length..];

        // Always one argument: a stray quote is percent-encoded, and a bare %1 gets quotes, so a
        // link can't split into extra arguments for the other app (review, 2026-10-23).
        var url = nxmUrl.Replace("\"", "%22", StringComparison.Ordinal);
        var arguments = rest.Contains("\"%1\"", StringComparison.Ordinal)
            ? rest.Replace("\"%1\"", $"\"{url}\"", StringComparison.Ordinal)
            : rest.Contains("%1", StringComparison.Ordinal)
                ? rest.Replace("%1", $"\"{url}\"", StringComparison.Ordinal)
                : $"{rest} \"{url}\"";
        return (exe, arguments.Trim());
    }

    /// <summary>The game an nxm:// link is for (nxm://&lt;game&gt;/mods/…), lower case; null if it isn't an nxm link.</summary>
    public static string? GameDomainOf(string nxmUrl) =>
        Uri.TryCreate(nxmUrl.Trim(), UriKind.Absolute, out var uri) && string.Equals(uri.Scheme, "nxm", StringComparison.OrdinalIgnoreCase)
            ? uri.Host.ToLowerInvariant()
            : null;

    public static bool IsForIcarus(string nxmUrl) => GameDomainOf(nxmUrl) == IcarusDomain;

    /// <summary>"crimsondesert, Stalker2HeartOfChornobyl" → lower-case domains; a pasted game page link (nexusmods.com/&lt;game&gt;/…) counts as its domain.</summary>
    public static IReadOnlyList<string> ParseGames(string? games) =>
        [.. (games ?? "")
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(game => DomainFromPageLink(game) ?? game.ToLowerInvariant())
            .Where(game => game != IcarusDomain)
            .Distinct()];

    /// <summary>Where another game's link goes: the app the user gave that game, else the app replaced last (fallback). Never a Starlink.</summary>
    public static string? ChooseHandler(string gameDomain, IReadOnlyList<NxmOtherApp> apps, string? fallback)
    {
        var domain = gameDomain.ToLowerInvariant();
        var chosen = apps.FirstOrDefault(app => app.Games.Contains(domain))?.Command ?? fallback;
        return IsStarlink(chosen) ? null : chosen;
    }

    private static string? DomainFromPageLink(string text)
    {
        var at = text.IndexOf("nexusmods.com/", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var rest = text[(at + "nexusmods.com/".Length)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var domain = (end < 0 ? rest : rest[..end]).ToLowerInvariant();
        return domain.Length > 0 && domain != "games" ? domain : null;
    }
}

/// <summary>Another mod manager Starlink replaced as the nxm:// handler: its command and the Nexus games whose links it gets.</summary>
public sealed record NxmOtherApp(string Command, IReadOnlyList<string> Games);
