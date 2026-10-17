using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Services;

public sealed record FirewallApplyResult(bool Success, string Message);

/// <summary>Reads inbound Windows Firewall rules and runs netsh as administrator. An interface so tests never touch the real firewall.</summary>
public interface IFirewallService
{
    /// <summary>Every inbound rule. Reading needs no administrator rights. Throws when the firewall can't be read.</summary>
    IReadOnlyList<FirewallRule> ReadInboundRules();

    /// <summary>The network type(s) this PC is on now (Public, Private, Domain) and whether the firewall is on for them.</summary>
    FirewallNetwork ReadCurrentNetwork();

    /// <summary>Runs the netsh commands in one elevated cmd.exe, so Windows asks for administrator approval once.</summary>
    Task<FirewallApplyResult> RunElevatedAsync(IReadOnlyList<string> netshCommands, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads through the firewall's own COM API (HNetCfg.FwPolicy2) rather than parsing netsh text,
/// which is translated on non-English Windows. Writing needs administrator rights, so changes go
/// through netsh in a cmd.exe started with the "runas" verb, every command on its command line
/// (no script file another program could swap before the elevated run reads it).
/// </summary>
public sealed class FirewallService : IFirewallService
{
    private const int DirectionInbound = 1;
    private const int ActionAllow = 1;
    private const int ErrorCancelled = 1223;

    private static dynamic OpenPolicy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Windows Firewall isn't available on this PC.");
        return Activator.CreateInstance(type)!;
    }

    public IReadOnlyList<FirewallRule> ReadInboundRules()
    {
        var policy = OpenPolicy();
        var rules = new List<FirewallRule>();
        foreach (dynamic rule in policy.Rules)
        {
            try
            {
                if ((int)rule.Direction != DirectionInbound)
                {
                    continue;
                }

                int profiles;
                try
                {
                    profiles = (int)rule.Profiles;
                }
                catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException or InvalidCastException)
                {
                    profiles = ServerFirewall.AllProfiles;
                }

                rules.Add(new FirewallRule(
                    (string?)rule.Name ?? "",
                    (bool)rule.Enabled,
                    Inbound: true,
                    Allow: (int)rule.Action == ActionAllow,
                    (int)rule.Protocol,
                    (string?)rule.LocalPorts ?? "*",
                    (string?)rule.ApplicationName,
                    profiles));
            }
            catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                // A rule type with no ports (ICMP and the like) — it can't open a game port anyway.
            }
        }

        return rules;
    }

    /// <summary>CurrentProfileTypes is a bit mask (several adapters can be on different network types); the firewall counts as on when it is on for any of them.</summary>
    public FirewallNetwork ReadCurrentNetwork()
    {
        var policy = OpenPolicy();
        var current = (int)policy.CurrentProfileTypes;
        if (current == 0)
        {
            return FirewallNetwork.Unknown;
        }

        var on = false;
        foreach (var bit in new[] { ServerFirewall.DomainProfile, ServerFirewall.PrivateProfile, ServerFirewall.PublicProfile })
        {
            if ((current & bit) != 0)
            {
                try
                {
                    on |= (bool)policy.FirewallEnabled[bit];
                }
                catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException or InvalidCastException)
                {
                    on = true; // can't tell: assume it is on, so nothing is called open that isn't
                }
            }
        }

        return new FirewallNetwork(current, on);
    }

    public async Task<FirewallApplyResult> RunElevatedAsync(IReadOnlyList<string> netshCommands, CancellationToken cancellationToken = default)
    {
        var arguments = ServerFirewall.BuildElevatedArguments(netshCommands);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                return new FirewallApplyResult(false, "Windows didn't start netsh.");
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new FirewallApplyResult(true, "Firewall updated.")
                : new FirewallApplyResult(false, $"netsh couldn't add a rule (exit code {process.ExitCode}).");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new FirewallApplyResult(false, "Administrator approval was declined, so nothing changed.");
        }
    }
}
