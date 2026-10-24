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

    /// <summary>"v1.9.0 · on", "off": like the game's column, so a switched-off mod reads as off (it used to show only the version, as if unticking hadn't worked).</summary>
    public string ServerText => Comparison.OnServer ? Describe(Comparison.ServerVersion, Comparison.OnOnServer) : "—";

    public string StateText => State switch
    {
        ServerModState.Same => "Same as your game",
        ServerModState.ServerOutOfDate => Comparison.GameVersion is not null && Comparison.ServerVersion is not null && Comparison.GameVersion != Comparison.ServerVersion
            ? "Server out of date"
            : "Server copy differs",
        ServerModState.ServerNewer => "Server has a newer version",
        ServerModState.NotOnServer => "Not on the server",
        // Removed from the Library (or never in the game): Remove is what takes it off; unticking only switches it off.
        ServerModState.OnlyOnServer => "Not in your game — Remove takes it off",
        _ => "",
    };

    /// <summary>The server's copy should be updated.</summary>
    public bool NeedsUpdate => State == ServerModState.ServerOutOfDate;

    /// <summary>Shown in the accent colour: out of date, or left on the server after it left your game.</summary>
    public bool NeedsAttention => NeedsUpdate || State == ServerModState.OnlyOnServer;

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
