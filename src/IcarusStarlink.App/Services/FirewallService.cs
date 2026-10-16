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

    /// <summary>Runs the netsh commands in one elevated script, so Windows asks for administrator approval once.</summary>
    Task<FirewallApplyResult> RunElevatedAsync(IReadOnlyList<string> netshCommands, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads through the firewall's own COM API (HNetCfg.FwPolicy2) rather than parsing netsh text,
/// which is translated on non-English Windows. Writing needs administrator rights, so changes go
/// through netsh in a script started with the "runas" verb.
/// </summary>
public sealed class FirewallService : IFirewallService
{
    private const int DirectionInbound = 1;
    private const int ActionAllow = 1;
    private const int ErrorCancelled = 1223;

    public IReadOnlyList<FirewallRule> ReadInboundRules()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Windows Firewall isn't available on this PC.");
        dynamic policy = Activator.CreateInstance(type)!;
        var rules = new List<FirewallRule>();
        foreach (dynamic rule in policy.Rules)
        {
            try
            {
                if ((int)rule.Direction != DirectionInbound)
                {
                    continue;
                }

                rules.Add(new FirewallRule(
                    (string?)rule.Name ?? "",
                    (bool)rule.Enabled,
                    Inbound: true,
                    Allow: (int)rule.Action == ActionAllow,
                    (int)rule.Protocol,
                    (string?)rule.LocalPorts ?? "*",
                    (string?)rule.ApplicationName));
            }
            catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                // A rule type with no ports (ICMP and the like) — it can't open a game port anyway.
            }
        }

        return rules;
    }

    public async Task<FirewallApplyResult> RunElevatedAsync(IReadOnlyList<string> netshCommands, CancellationToken cancellationToken = default)
    {
        var script = Path.Combine(Path.GetTempPath(), $"IcarusStarlink-firewall-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(script, ServerFirewall.BuildScript(netshCommands), cancellationToken);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\"\"",
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
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch (IOException)
            {
            }
        }
    }
}
