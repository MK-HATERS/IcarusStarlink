namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// Registers/unregisters this app as the OS's handler for nxm:// links — a real, hard-to-reverse
/// registry write, so it's never called automatically; only from an explicit Settings action the
/// user clicks themselves, same gating philosophy as the real game-folder install.
/// </summary>
public interface INxmProtocolRegistrar
{
    /// <summary>True if nxm:// links are currently registered to launch THIS app's own exe specifically (not just "something" — a different mod manager, e.g. Vortex, may hold the registration instead).</summary>
    bool IsRegisteredToThisApp();

    void Register();

    void Unregister();
}
