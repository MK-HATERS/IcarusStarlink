namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// Registers/unregisters this app as the OS's handler for nxm:// links — a real, hard-to-reverse
/// registry write, so it's never called automatically; only from an explicit Settings action the
/// user clicks themselves (or their own "take them back automatically" switch), same gating
/// philosophy as the real game-folder install.
/// </summary>
public interface INxmProtocolRegistrar
{
    /// <summary>True if nxm:// links are currently registered to launch THIS app's own exe specifically (not just "something" — a different mod manager, e.g. Vortex, may hold the registration instead).</summary>
    bool IsRegisteredToThisApp();

    /// <summary>The command Windows runs for an nxm:// link right now (see NxmHandlerCommand), or null when nothing handles them.</summary>
    string? CurrentHandlerCommand();

    /// <summary>The other mod manager's command Register last replaced — where links for other games are handed on. Null if there was none.</summary>
    string? PreviousHandlerCommand();

    /// <summary>Every other mod manager Starlink has replaced, with the games the user gave it (Settings › Nexus).</summary>
    IReadOnlyList<NxmOtherApp> OtherApps() => [];

    /// <summary>Sets which games' links go to this app ("crimsondesert, stalker2heartofchornobyl").</summary>
    void SetOtherAppGames(string command, string games)
    {
    }

    void RemoveOtherApp(string command)
    {
    }

    /// <summary>Where a link for this game goes (NxmHandlerCommand.ChooseHandler): the app given that game, else the one replaced last.</summary>
    string? HandlerFor(string gameDomain) => NxmHandlerCommand.ChooseHandler(gameDomain, OtherApps(), PreviousHandlerCommand());

    /// <summary>Makes this app the handler, first remembering the one it replaces (unless that's a Starlink, see NxmHandlerCommand.IsStarlink).</summary>
    void Register();

    void Unregister();
}
