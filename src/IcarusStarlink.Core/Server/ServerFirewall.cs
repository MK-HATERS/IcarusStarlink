using System.Globalization;

namespace IcarusStarlink.Core.Server;

/// <summary>
/// One Windows Firewall rule as the firewall reports it (read through its COM API, so it is the
/// same on every Windows language). Profiles is the network types it applies to (Domain 1,
/// Private 2, Public 4; all of them by default).
/// </summary>
public sealed record FirewallRule(
    string Name, bool Enabled, bool Inbound, bool Allow, int Protocol, string LocalPorts, string? Program, int Profiles = ServerFirewall.AllProfiles,
    bool AppScoped = false);

/// <summary>The network type(s) Windows says this PC is on right now, and whether the firewall is on for them.</summary>
public sealed record FirewallNetwork(int Profiles, bool FirewallOn)
{
    public static readonly FirewallNetwork Unknown = new(ServerFirewall.AllProfiles, FirewallOn: true);

    public string Name => ServerFirewall.ProfileNames(Profiles);
}

/// <summary>
/// Whether one port the server needs gets through on the current network, and which rule decides
/// it. OtherNetworksRule is an allow rule that would open it, but only on other network types
/// (a Private-only rule while this PC is on a Public network).
/// </summary>
public sealed record FirewallPortStatus(string Label, int Port, int Protocol, bool Open, bool Blocked, string? RuleName,
    string? OtherNetworksRule = null, string? CurrentNetwork = null, string? OtherNetworks = null)
{
    public string ProtocolName => ServerFirewall.ProtocolName(Protocol);

    public string Summary => Blocked
        ? $"{Label} {Port}/{ProtocolName}: BLOCKED by '{RuleName}'"
        : Open
            ? $"{Label} {Port}/{ProtocolName}: open ('{RuleName}')"
            : OtherNetworksRule is not null
                ? $"{Label} {Port}/{ProtocolName}: blocked on your current network ({CurrentNetwork}). '{OtherNetworksRule}' only covers {OtherNetworks} networks"
                : $"{Label} {Port}/{ProtocolName}: not open";
}

/// <summary>
/// Inbound firewall rules for a dedicated server. Players need the game port and the query port
/// open over UDP (official defaults 17777 and 27015); TCP on the game port is optional. A rule
/// counts when it is enabled, inbound, applies to the network type the PC is on now, covers the
/// port and protocol, and either names no program or names this server's
/// IcarusServer-Win64-Shipping.exe. A matching BLOCK rule wins over any allow rule — Windows
/// creates those when someone clicks Cancel on its "allow access" prompt. Rules Starlink adds carry
/// the profile's id in their name, so changing a port (or renaming the profile) replaces them.
/// </summary>
public static class ServerFirewall
{
    public const int Tcp = 6;
    public const int Udp = 17;
    public const int AnyProtocol = 256;

    public const int DomainProfile = 1;
    public const int PrivateProfile = 2;
    public const int PublicProfile = 4;
    public const int AllProfiles = 0x7FFFFFFF;

    public static string ProtocolName(int protocol) => protocol switch
    {
        Tcp => "TCP",
        Udp => "UDP",
        _ => "any",
    };

    /// <summary>"Public", "Private and Public", "all"…</summary>
    public static string ProfileNames(int profiles)
    {
        const int every = DomainProfile | PrivateProfile | PublicProfile;
        if ((profiles & every) == every)
        {
            return "all";
        }

        var names = new List<string>();
        if ((profiles & DomainProfile) != 0)
        {
            names.Add("Domain");
        }

        if ((profiles & PrivateProfile) != 0)
        {
            names.Add("Private");
        }

        if ((profiles & PublicProfile) != 0)
        {
            names.Add("Public");
        }

        return names.Count == 0 ? "no" : string.Join(" and ", names);
    }

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

    /// <summary>
    /// <paramref name="network"/> is the network type(s) the PC is on now: only rules for those
    /// count. With the firewall off on the current network every port is open.
    /// </summary>
    public static FirewallPortStatus Check(IEnumerable<FirewallRule> rules, string serverExe, string label, int port, int protocol,
        FirewallNetwork? network = null)
    {
        network ??= FirewallNetwork.Unknown;
        if (!network.FirewallOn)
        {
            return new FirewallPortStatus(label, port, protocol, Open: true, Blocked: false, $"Windows Firewall is off on {network.Name} networks");
        }

        FirewallRule? allow = null;
        FirewallRule? elsewhere = null;
        foreach (var rule in rules)
        {
            // A rule tied to an app package or owned by one user's app (the Microsoft Store's own
            // allow-everything rules) only applies to that app, never to the server.
            if (!rule.Enabled || !rule.Inbound || rule.AppScoped
                || (rule.Protocol != protocol && rule.Protocol != AnyProtocol)
                || !PortMatches(rule.LocalPorts, port)
                || !ProgramMatches(rule.Program, serverExe))
            {
                continue;
            }

            if ((rule.Profiles & network.Profiles) == 0)
            {
                if (rule.Allow)
                {
                    elsewhere ??= rule;
                }

                continue;
            }

            if (!rule.Allow)
            {
                return new FirewallPortStatus(label, port, protocol, Open: false, Blocked: true, rule.Name);
            }

            allow ??= rule;
        }

        return allow is not null
            ? new FirewallPortStatus(label, port, protocol, Open: true, Blocked: false, allow.Name)
            : new FirewallPortStatus(label, port, protocol, Open: false, Blocked: false, null,
                elsewhere?.Name, network.Name, elsewhere is null ? null : ProfileNames(elsewhere.Profiles));
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
        return $"Icarus Starlink - {(safe.Length > 0 ? safe : "server")} ({ProfileKey(profile)}) - {label} {ProtocolName(protocol)}";
    }

    /// <summary>The first 8 hex digits of the profile id: what ties a rule (or a backups folder) to its profile, whatever the profile is called now.</summary>
    public static string ProfileKey(DedicatedServerProfile profile) => profile.Id.ToString("N")[..8];

    /// <summary>
    /// A rule Starlink made for this profile, matched by the profile's id in the name — renaming the
    /// profile changes the rest of the name, and the rules made under the old name must still be found.
    /// </summary>
    public static bool IsOwnRule(string ruleName, DedicatedServerProfile profile) =>
        ruleName.StartsWith("Icarus Starlink - ", StringComparison.Ordinal)
        && ruleName.Contains($" ({ProfileKey(profile)}) - ", StringComparison.OrdinalIgnoreCase);

    /// <summary>The names of the rules Starlink made for this profile that exist now (under any name the profile has had).</summary>
    public static IReadOnlyList<string> OwnRules(IEnumerable<FirewallRule> rules, DedicatedServerProfile profile) =>
        [.. rules.Select(r => r.Name).Where(n => IsOwnRule(n, profile)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every rule name Starlink may have made for this profile under its current name (any port it could have used), for removal.</summary>
    public static IEnumerable<string> OwnRuleNames(DedicatedServerProfile profile) =>
    [
        RuleName(profile, "Game port", Udp),
        RuleName(profile, "Query port", Udp),
        RuleName(profile, "Game port", Tcp),
    ];

    /// <summary>
    /// netsh arguments that make the firewall match: Starlink's old rules for this profile are
    /// deleted first (a changed port must not leave the old one open) — the current names plus
    /// <paramref name="existingOwnRules"/>, the ones found under an older profile name — then a rule
    /// is added for each port in <paramref name="toOpen"/>, scoped to this server's exe.
    /// </summary>
    public static IReadOnlyList<string> BuildNetshCommands(DedicatedServerProfile profile, string serverExe, IEnumerable<(string Label, int Port, int Protocol)> toOpen,
        IEnumerable<string>? existingOwnRules = null)
    {
        var names = OwnRuleNames(profile).Concat(existingOwnRules ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        var commands = names.Select(name => $"advfirewall firewall delete rule name=\"{name}\"").ToList();
        foreach (var (label, port, protocol) in toOpen)
        {
            commands.Add(
                $"advfirewall firewall add rule name=\"{RuleName(profile, label, protocol)}\" dir=in action=allow "
                + $"protocol={ProtocolName(protocol)} localport={port.ToString(CultureInfo.InvariantCulture)} "
                + $"program=\"{serverExe}\" enable=yes profile=any");
        }

        return commands;
    }

    /// <summary>
    /// The arguments for ONE elevated cmd.exe that runs every netsh command, so Windows asks for
    /// administrator approval once — passed on its command line rather than through a script file
    /// in TEMP, which another program could swap between writing it and the elevated run. Deletes
    /// may fail (nothing to delete); a failed add ends it with exit code 1. Characters cmd would
    /// still act on inside quotes (% ! ^, a line break, a stray quote) are refused rather than escaped.
    /// </summary>
    public static string BuildElevatedArguments(IReadOnlyList<string> netshCommands)
    {
        var parts = new List<string>();
        foreach (var command in netshCommands)
        {
            if (command.IndexOfAny(['%', '!', '^', '\r', '\n']) >= 0 || command.Count(c => c == '"') % 2 != 0)
            {
                throw new ArgumentException("A firewall rule name or the server's folder contains a character Windows' command line can't pass safely (% ! ^ or a quote). Move the server to a folder without it, or add the rules in Windows Defender Firewall yourself.");
            }

            parts.Add(command.Contains(" delete rule ", StringComparison.Ordinal)
                ? $"netsh {command} >nul 2>&1"
                : $"netsh {command} >nul || exit 1");
        }

        parts.Add("exit 0");
        return $"/d /s /c \"{string.Join(" & ", parts)}\"";
    }
}
