using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One UE4SS mod in the server's table: the game's copy, the server's copy, how they compare, and
/// what to do about it (user request 2026-10-23 — the page used to list the same mods twice and
/// never said a server copy was out of date). ServerOn is the server's own on/off switch.
/// </summary>
public sealed partial class ServerUe4ssRow : ObservableObject
{
    private readonly Action<ServerUe4ssRow, bool>? _onServerToggled;
    private bool _applying;

    public ServerUe4ssRow(ServerUe4ssComparison comparison, Action<ServerUe4ssRow, bool>? onServerToggled = null)
    {
        Comparison = comparison;
        _onServerToggled = onServerToggled;
        _serverOn = comparison.OnOnServer;
    }

    public ServerUe4ssComparison Comparison { get; }

    public string Name => Comparison.Name;

    public ServerModState State => Comparison.State;

    public bool IsOnServer => Comparison.OnServer;

    /// <summary>"v1.9.0 · on", "off", or "—" when the game doesn't have it.</summary>
    public string GameText => Comparison.InGame ? Describe(Comparison.GameVersion, Comparison.OnInGame) : "—";

    public string ServerText => Comparison.OnServer ? (Comparison.ServerVersion is { } v ? $"v{v}" : "") : "—";

    public string StateText => State switch
    {
        ServerModState.Same => "Same as your game",
        ServerModState.ServerOutOfDate => Comparison.GameVersion is not null && Comparison.ServerVersion is not null && Comparison.GameVersion != Comparison.ServerVersion
            ? "Server out of date"
            : "Server copy differs",
        ServerModState.ServerNewer => "Server has a newer version",
        ServerModState.NotOnServer => "Not on the server",
        ServerModState.OnlyOnServer => "Only on the server",
        _ => "",
    };

    /// <summary>Shown in the accent colour: the server's copy should be updated.</summary>
    public bool NeedsUpdate => State == ServerModState.ServerOutOfDate;

    public bool IsSame => State == ServerModState.Same;

    /// <summary>The row's main button: put the game's copy on the server ("Copy to server" / "Update on server"); none when they match or the game doesn't have it.</summary>
    public string? CopyActionLabel => State switch
    {
        ServerModState.NotOnServer => "Copy to server",
        ServerModState.ServerOutOfDate => "Update on server",
        ServerModState.ServerNewer => "Use your game's copy",
        _ => null,
    };

    public bool HasCopyAction => CopyActionLabel is not null;

    [ObservableProperty]
    private bool _serverOn;

    partial void OnServerOnChanged(bool value)
    {
        if (!_applying)
        {
            _onServerToggled?.Invoke(this, value);
        }
    }

    /// <summary>Puts the switch back without asking the server again (a refused change).</summary>
    public void ResetServerOn(bool value)
    {
        _applying = true;
        ServerOn = value;
        _applying = false;
    }

    private static string Describe(string? version, bool on) => version is null ? (on ? "on" : "off") : $"v{version} · {(on ? "on" : "off")}";
}
