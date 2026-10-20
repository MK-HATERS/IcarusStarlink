using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 4: Windows Firewall. Shows whether the ports this server uses get through, counting
/// rules the owner already made (by hand or from Windows' own "allow access" prompt), and adds
/// only what's missing, with one administrator prompt. The ports start as the profile's own
/// and can be changed here. Router port forwarding is outside the PC, so it stays a hint.
/// </summary>
public sealed partial class LocalServerViewModel
{
    [ObservableProperty] private string _firewallGamePortText = "";
    [ObservableProperty] private string _firewallQueryPortText = "";
    [ObservableProperty] private bool _firewallIncludeTcp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFirewallPortsCommand), nameof(RemoveFirewallRulesCommand))]
    private bool _isFirewallBusy;

    [ObservableProperty] private string? _firewallSummary;

    /// <summary>The Windows firewall section's short header: "All ports open", "2 port(s) not open"... Empty until read.</summary>
    [ObservableProperty] private string _firewallState = "";

    /// <summary>"This PC is on a Public network." Rules only count for the network type the PC is on.</summary>
    [ObservableProperty] private string? _firewallNetworkText;

    public ObservableCollection<FirewallPortStatus> FirewallPorts { get; } = [];

    /// <summary>True when one of the server's ports is shut by a block rule (adding an allow rule can't fix that).</summary>
    [ObservableProperty] private bool _firewallHasBlockRule;

    private IReadOnlyList<FirewallRule> _firewallRules = [];
    private FirewallNetwork _firewallNetwork = FirewallNetwork.Unknown;
    private int _firewallReadVersion;

    /// <summary>The read started when a server is selected. Tests await it instead of racing it.</summary>
    internal Task FirewallLoadTask { get; private set; } = Task.CompletedTask;

    partial void OnFirewallGamePortTextChanged(string value) => RecheckFirewall();
    partial void OnFirewallQueryPortTextChanged(string value) => RecheckFirewall();
    partial void OnFirewallIncludeTcpChanged(bool value) => RecheckFirewall();

    /// <summary>On selecting a server: its own ports, and TCP on the game port ticked when a rule for it already exists.</summary>
    private void LoadFirewall()
    {
        FirewallPorts.Clear();
        FirewallSummary = null;
        FirewallState = "";
        if (SelectedProfile is not { } p)
        {
            return;
        }

        FirewallGamePortText = p.Port.ToString(CultureInfo.InvariantCulture);
        FirewallQueryPortText = p.QueryPort.ToString(CultureInfo.InvariantCulture);
        FirewallLoadTask = RefreshFirewallAsync(detectTcp: true);
    }

    /// <summary>A port changed in Settings moves the firewall ports with it, unless they were set to something else on purpose.</summary>
    private void SyncFirewallPortsAfterSave(DedicatedServerProfile before, DedicatedServerProfile after)
    {
        if (FirewallGamePortText == before.Port.ToString(CultureInfo.InvariantCulture))
        {
            FirewallGamePortText = after.Port.ToString(CultureInfo.InvariantCulture);
        }

        if (FirewallQueryPortText == before.QueryPort.ToString(CultureInfo.InvariantCulture))
        {
            FirewallQueryPortText = after.QueryPort.ToString(CultureInfo.InvariantCulture);
        }
    }

    [RelayCommand]
    private Task RefreshFirewall() => RefreshFirewallAsync(detectTcp: false);

    internal async Task RefreshFirewallAsync(bool detectTcp)
    {
        if (_firewall is null || SelectedProfile is null)
        {
            FirewallSummary = "Firewall status isn't available.";
            FirewallState = "Not available";
            return;
        }

        var version = ++_firewallReadVersion;
        IReadOnlyList<FirewallRule> rules;
        FirewallNetwork network;
        try
        {
            (rules, network) = await Task.Run(() => (_firewall.ReadInboundRules(), ReadNetworkOrUnknown(_firewall)));
        }
        catch (Exception ex)
        {
            rules = [];
            if (version == _firewallReadVersion)
            {
                _firewallRules = rules;
                FirewallSummary = $"Couldn't read Windows Firewall: {ex.Message}";
                FirewallState = "Couldn't read it";
            }

            return;
        }

        // A newer read (another server selected, Refresh clicked) has started: this answer is stale.
        if (version != _firewallReadVersion)
        {
            return;
        }

        _firewallRules = rules;
        _firewallNetwork = network;

        if (detectTcp && TryFirewallPorts(out var game, out _) && SelectedProfile is { } p)
        {
            FirewallIncludeTcp = ServerFirewall.Check(_firewallRules, DedicatedServerLayout.ShippingExePath(p.InstallFolder), "Game port", game, ServerFirewall.Tcp, _firewallNetwork).Open;
        }

        RecheckFirewall();
    }

    private void RecheckFirewall()
    {
        FirewallPorts.Clear();
        FirewallHasBlockRule = false;
        if (SelectedProfile is not { } p)
        {
            return;
        }

        if (!TryFirewallPorts(out var game, out var query))
        {
            FirewallSummary = "Ports must be numbers from 1 to 65535.";
            FirewallState = "Check the ports";
            return;
        }

        var exe = DedicatedServerLayout.ShippingExePath(p.InstallFolder);
        foreach (var (label, port, protocol) in ServerFirewall.RequiredPorts(game, query, FirewallIncludeTcp))
        {
            FirewallPorts.Add(ServerFirewall.Check(_firewallRules, exe, label, port, protocol, _firewallNetwork));
        }

        FirewallHasBlockRule = FirewallPorts.Any(s => s.Blocked);
        FirewallNetworkText = _firewallNetwork == FirewallNetwork.Unknown
            ? null
            : _firewallNetwork.FirewallOn
                ? $"This PC is on a {_firewallNetwork.Name} network; only rules for that network type count."
                : $"Windows Firewall is off for the {_firewallNetwork.Name} network this PC is on.";
        var missing = FirewallPorts.Count(s => !s.Open);
        var otherNetworksOnly = FirewallPorts.Any(s => !s.Open && s.OtherNetworksRule is not null);
        FirewallSummary = FirewallHasBlockRule
            ? "A block rule shuts one of these ports. Remove or disable it in Windows Defender Firewall (wf.msc); an allow rule can't override it."
            : missing == 0
                ? "Windows Firewall lets players in on every port this server needs."
                : otherNetworksOnly
                    ? $"{missing} port(s) aren't open on the network this PC is on now ({_firewallNetwork.Name}): a rule opens them only on other network types. 'Open these ports' adds rules for every network type."
                    : $"{missing} port(s) aren't open yet. 'Open these ports' adds the missing rules.";

        FirewallState = FirewallHasBlockRule
            ? "A block rule shuts a port"
            : missing == 0
                ? "All ports open"
                : $"{missing} port(s) not open";

        if (game != p.Port || query != p.QueryPort)
        {
            FirewallSummary += $" Note: the server itself uses {p.Port} and {p.QueryPort} (Settings).";
        }
    }

    private bool TryFirewallPorts(out int game, out int query)
    {
        query = 0;
        return TryParsePort(FirewallGamePortText, out game) & TryParsePort(FirewallQueryPortText, out query);
    }

    private bool CanChangeFirewall() => _firewall is not null && SelectedProfile is not null && !IsFirewallBusy;

    /// <summary>Adds an allow rule (scoped to this server's exe) for each port not already open. Ports open only through Starlink's own earlier rules are re-added, since those are replaced as a set.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeFirewall))]
    private async Task OpenFirewallPortsAsync()
    {
        if (_firewall is null || SelectedProfile is not { } p || !TryFirewallPorts(out var game, out var query))
        {
            FirewallSummary = "Ports must be numbers from 1 to 65535.";
            return;
        }

        await RefreshFirewallAsync(detectTcp: false);
        if (!FirewallPorts.Any(s => !s.Open && !s.Blocked))
        {
            if (!FirewallHasBlockRule)
            {
                FirewallSummary = "Nothing to add: every port is already open.";
            }

            return;
        }

        // Starlink's own rules are deleted and re-added as a set, so a port they already open is re-added too.
        var existingOwn = ServerFirewall.OwnRules(_firewallRules, p);
        var own = existingOwn.Concat(ServerFirewall.OwnRuleNames(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toOpen = FirewallPorts
            .Where(s => !s.Blocked && (!s.Open || (s.RuleName is not null && own.Contains(s.RuleName))))
            .Select(s => (s.Label, s.Port, s.Protocol))
            .ToList();
        var list = string.Join("\n", toOpen.Select(t => $"  {t.Label} {t.Port}/{ServerFirewall.ProtocolName(t.Protocol)}"));
        if (!_dialogService.Confirm(
                $"Add Windows Firewall rules so players can reach '{p.Name}'?\n\n{list}\n\nInbound only, and only for this server's IcarusServer-Win64-Shipping.exe. Windows asks for administrator approval once.",
                "Open firewall ports", ThemedConfirmSeverity.Question))
        {
            return;
        }

        await RunFirewallAsync(ServerFirewall.BuildNetshCommands(p, DedicatedServerLayout.ShippingExePath(p.InstallFolder), toOpen, existingOwn),
            $"Opened {toOpen.Count} firewall port(s) for '{p.Name}'.");
    }

    [RelayCommand(CanExecute = nameof(CanChangeFirewall))]
    private async Task RemoveFirewallRulesAsync()
    {
        if (_firewall is null || SelectedProfile is not { } p)
        {
            return;
        }

        var own = ServerFirewall.OwnRules(_firewallRules, p);
        if (own.Count == 0)
        {
            FirewallSummary = "Starlink hasn't added any firewall rules for this server. Rules you made yourself are left alone.";
            return;
        }

        if (!_dialogService.Confirm(
                $"Remove the firewall rules Starlink added for '{p.Name}'?\n\nRules you made yourself are not touched. Windows asks for administrator approval once.",
                "Remove firewall rules", ThemedConfirmSeverity.Question))
        {
            return;
        }

        await RunFirewallAsync(ServerFirewall.BuildNetshCommands(p, DedicatedServerLayout.ShippingExePath(p.InstallFolder), [], own),
            $"Removed Starlink's firewall rules for '{p.Name}'.");
    }

    /// <summary>A failed network read must not hide the rules: the check then counts every rule, as before.</summary>
    private static FirewallNetwork ReadNetworkOrUnknown(IFirewallService firewall)
    {
        try
        {
            return firewall.ReadCurrentNetwork();
        }
        catch (Exception)
        {
            return FirewallNetwork.Unknown;
        }
    }

    /// <summary>The rules Starlink made for a profile, read fresh (the card's list belongs to whichever server is selected). Empty when the firewall can't be read.</summary>
    private async Task<IReadOnlyList<string>> ReadOwnFirewallRulesAsync(DedicatedServerProfile profile)
    {
        if (_firewall is null)
        {
            return [];
        }

        try
        {
            var rules = await Task.Run(_firewall.ReadInboundRules);
            return ServerFirewall.OwnRules(rules, profile);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Removes the given rules (Starlink's own, for a profile being removed from the list) with one administrator prompt.</summary>
    private async Task<bool> RemoveOwnFirewallRulesAsync(DedicatedServerProfile profile, IReadOnlyList<string> ruleNames)
    {
        if (_firewall is null || ruleNames.Count == 0)
        {
            return true;
        }

        IsFirewallBusy = true;
        try
        {
            var result = await _firewall.RunElevatedAsync(ServerFirewall.BuildNetshCommands(profile, DedicatedServerLayout.ShippingExePath(profile.InstallFolder), [], ruleNames));
            _activityLog.Log(result.Success
                    ? $"Removed the {ruleNames.Count} firewall rule(s) Starlink added for '{profile.Name}'."
                    : $"The firewall rules Starlink added for '{profile.Name}' were left in place: {result.Message}",
                result.Success ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
            return result.Success;
        }
        catch (Exception ex)
        {
            _activityLog.Log($"The firewall rules Starlink added for '{profile.Name}' were left in place: {ex.Message}", ActivityEntryKind.Warning);
            return false;
        }
        finally
        {
            IsFirewallBusy = false;
        }
    }

    private async Task RunFirewallAsync(IReadOnlyList<string> commands, string successMessage)
    {
        IsFirewallBusy = true;
        try
        {
            var result = await _firewall!.RunElevatedAsync(commands);
            // Not "not changed": Starlink's own old rules are deleted before the new ones are
            // added, so a failed add can leave the firewall different from before.
            StatusMessage = result.Success ? successMessage : $"Firewall: {result.Message.TrimEnd('.')}. The list below shows what's open now.";
            _activityLog.Log(StatusMessage, result.Success ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't change the firewall: {ex.Message}";
        }
        finally
        {
            IsFirewallBusy = false;
        }

        await RefreshFirewallAsync(detectTcp: false);
    }
}
