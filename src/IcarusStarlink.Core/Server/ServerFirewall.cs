using System.Globalization;

namespace IcarusStarlink.Core.Server;

/// <summary>One Windows Firewall rule as the firewall reports it (read through its COM API, so it is the same on every Windows language).</summary>
public sealed record FirewallRule(
    string Name, bool Enabled, bool Inbound, bool Allow, int Protocol, string LocalPorts, string? Program);

/// <summary>Whether one port the server needs gets through, and which rule decides it.</summary>
public sealed record FirewallPortStatus(string Label, int Port, int Protocol, bool Open, bool Blocked, string? RuleName)
{
    public string ProtocolName => ServerFirewall.ProtocolName(Protocol);

    public string Summary => Blocked
        ? $"{Label} {Port}/{ProtocolName}: BLOCKED by '{RuleName}'"
        : Open
            ? $"{Label} {Port}/{ProtocolName}: open ('{RuleName}')"
            : $"{Label} {Port}/{ProtocolName}: not open";
}

/// <summary>
/// Inbound firewall rules for a dedicated server. Players need the game port and the query port
/// open over UDP (official defaults 17777 and 27015); TCP on the game port is optional. A rule
/// counts when it is enabled, inbound, covers the port and protocol, and either names no program
/// or names this server's IcarusServer-Win64-Shipping.exe. A matching BLOCK rule wins over any
/// allow rule — Windows creates those when someone clicks Cancel on its "allow access" prompt.
/// Rules Starlink adds carry a fixed name per profile, so changing a port replaces them.
/// </summary>
public static class ServerFirewall
{
    public const int Tcp = 6;
    public const int Udp = 17;
    public const int AnyProtocol = 256;

    public static string ProtocolName(int protocol) => protocol switch
    {
        Tcp => "TCP",
        Udp => "UDP",
        _ => "any",
    };

    /// <summary>The ports a profile needs, in the order the card shows them.</summary>
    public static IReadOnlyList<(string Label, int Port, int Protocol)> RequiredPorts(int gamePort, int queryPort, bool includeGameTcp)
    {
        var list = new List<(string, int, int)> { ("Game port", gamePort, Udp), ("Query port", queryPort, Udp) };
        if (includeGameTcp)
        {
            list.Add(("Game port", gamePort, Tcp));
        }

        return list;
    }

    public static FirewallPortStatus Check(IEnumerable<FirewallRule> rules, string serverExe, string label, int port, int protocol)
    {
        FirewallRule? allow = null;
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !rule.Inbound
                || (rule.Protocol != protocol && rule.Protocol != AnyProtocol)
                || !PortMatches(rule.LocalPorts, port)
                || !ProgramMatches(rule.Program, serverExe))
            {
                continue;
            }

            if (!rule.Allow)
            {
                return new FirewallPortStatus(label, port, protocol, Open: false, Blocked: true, rule.Name);
            }

            allow ??= rule;
        }

        return new FirewallPortStatus(label, port, protocol, allow is not null, Blocked: false, allow?.Name);
    }

    /// <summary>"*", "Any", a single port, a range ("27015-27020") or a comma list of those.</summary>
    public static bool PortMatches(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts))
        {
            return true;
        }

        foreach (var raw in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw is "*" || raw.Equals("Any", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var dash = raw.IndexOf('-');
            if (dash > 0
                && int.TryParse(raw[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var low)
                && int.TryParse(raw[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var high))
            {
                if (port >= low && port <= high)
                {
                    return true;
                }
            }
            else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single) && single == port)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>No program (or "*") means the rule applies to every program.</summary>
    public static bool ProgramMatches(string? program, string serverExe)
    {
        if (string.IsNullOrWhiteSpace(program) || program == "*")
        {
            return true;
        }

        return string.Equals(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(program.Trim())),
            Path.GetFullPath(serverExe),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name Starlink gives its own rule for this profile and port, e.g. "Icarus Starlink - Friends server (1a2b3c4d) - Game port UDP".</summary>
    public static string RuleName(DedicatedServerProfile profile, string label, int protocol)
    {
        var safe = new string([.. profile.Name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')]).Trim();
        return $"Icarus Starlink - {(safe.Length > 0 ? safe : "server")} ({profile.Id.ToString("N")[..8]}) - {label} {ProtocolName(protocol)}";
    }

    /// <summary>Every rule name Starlink may have made for this profile (any port it could have used), for removal.</summary>
    public static IEnumerable<string> OwnRuleNames(DedicatedServerProfile profile) =>
    [
        RuleName(profile, "Game port", Udp),
        RuleName(profile, "Query port", Udp),
        RuleName(profile, "Game port", Tcp),
    ];

    /// <summary>
    /// netsh arguments that make the firewall match: Starlink's old rules for this profile are
    /// deleted first (a changed port must not leave the old one open), then a rule is added for
    /// each port in <paramref name="toOpen"/>, scoped to this server's exe.
    /// </summary>
    public static IReadOnlyList<string> BuildNetshCommands(DedicatedServerProfile profile, string serverExe, IEnumerable<(string Label, int Port, int Protocol)> toOpen)
    {
        var commands = OwnRuleNames(profile).Select(name => $"advfirewall firewall delete rule name=\"{name}\"").ToList();
        foreach (var (label, port, protocol) in toOpen)
        {
            commands.Add(
                $"advfirewall firewall add rule name=\"{RuleName(profile, label, protocol)}\" dir=in action=allow "
                + $"protocol={ProtocolName(protocol)} localport={port.ToString(CultureInfo.InvariantCulture)} "
                + $"program=\"{serverExe}\" enable=yes profile=any");
        }

        return commands;
    }

    /// <summary>The script run elevated: deletes may fail (nothing to delete), adds must not.</summary>
    public static string BuildScript(IReadOnlyList<string> netshCommands)
    {
        var lines = new List<string> { "@echo off" };
        foreach (var command in netshCommands)
        {
            lines.Add(command.Contains(" delete rule ", StringComparison.Ordinal)
                ? $"netsh {command} >nul 2>&1"
                : $"netsh {command} >nul || exit /b 1");
        }

        lines.Add("exit /b 0");
        return string.Join("\r\n", lines) + "\r\n";
    }
}
