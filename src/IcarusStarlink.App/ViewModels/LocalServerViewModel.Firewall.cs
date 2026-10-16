using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public ObservableCollection<FirewallPortStatus> FirewallPorts { get; } = [];

    /// <summary>True when one of the server's ports is shut by a block rule (adding an allow rule can't fix that).</summary>
    [ObservableProperty] private bool _firewallHasBlockRule;

    private IReadOnlyList<FirewallRule> _firewallRules = [];
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
            return;
        }

        var version = ++_firewallReadVersion;
        IReadOnlyList<FirewallRule> rules;
        try
        {
            rules = await Task.Run(_firewall.ReadInboundRules);
        }
        catch (Exception ex)
        {
            rules = [];
            if (version == _firewallReadVersion)
            {
                _firewallRules = rules;
                FirewallSummary = $"Couldn't read Windows Firewall: {ex.Message}";
            }

            return;
        }

        // A newer read (another server selected, Refresh clicked) has started: this answer is stale.
        if (version != _firewallReadVersion)
        {
            return;
        }

        _firewallRules = rules;

        if (detectTcp && TryFirewallPorts(out var game, out _) && SelectedProfile is { } p)
        {
            FirewallIncludeTcp = ServerFirewall.Check(_firewallRules, DedicatedServerLayout.ShippingExePath(p.InstallFolder), "Game port", game, ServerFirewall.Tcp).Open;
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
            return;
        }

        var exe = DedicatedServerLayout.ShippingExePath(p.InstallFolder);
        foreach (var (label, port, protocol) in ServerFirewall.RequiredPorts(game, query, FirewallIncludeTcp))
        {
            FirewallPorts.Add(ServerFirewall.Check(_firewallRules, exe, label, port, protocol));
        }

        FirewallHasBlockRule = FirewallPorts.Any(s => s.Blocked);
        var missing = FirewallPorts.Count(s => !s.Open);
        FirewallSummary = FirewallHasBlockRule
            ? "A block rule shuts one of these ports. Remove or disable it in Windows Defender Firewall (wf.msc); an allow rule can't override it."
            : missing == 0
                ? "Windows Firewall lets players in on every port this server needs."
                : $"{missing} port(s) aren't open yet. 'Open these ports' adds the missing rules.";

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
        var own = ServerFirewall.OwnRuleNames(p).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        await RunFirewallAsync(ServerFirewall.BuildNetshCommands(p, DedicatedServerLayout.ShippingExePath(p.InstallFolder), toOpen),
            $"Opened {toOpen.Count} firewall port(s) for '{p.Name}'.");
    }

    [RelayCommand(CanExecute = nameof(CanChangeFirewall))]
    private async Task RemoveFirewallRulesAsync()
    {
        if (_firewall is null || SelectedProfile is not { } p)
        {
            return;
        }

        var own = ServerFirewall.OwnRuleNames(p).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!_firewallRules.Any(r => own.Contains(r.Name)))
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

        await RunFirewallAsync(ServerFirewall.BuildNetshCommands(p, DedicatedServerLayout.ShippingExePath(p.InstallFolder), []),
            $"Removed Starlink's firewall rules for '{p.Name}'.");
    }

    private async Task RunFirewallAsync(IReadOnlyList<string> commands, string successMessage)
    {
        IsFirewallBusy = true;
        try
        {
            var result = await _firewall!.RunElevatedAsync(commands);
            StatusMessage = result.Success ? successMessage : $"Firewall not changed: {result.Message}";
            _activityLog.Log(StatusMessage, result.Success ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Firewall not changed: {ex.Message}";
        }
        finally
        {
            IsFirewallBusy = false;
        }

        await RefreshFirewallAsync(detectTcp: false);
    }
}
