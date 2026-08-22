using System.Runtime.Versioning;
using IcarusStarlink.Core.Nexus;
using Microsoft.Win32;

namespace IcarusStarlink.Storage.Nexus;

/// <summary>
/// Registers this app as the per-user nxm:// protocol handler — under HKEY_CURRENT_USER\Software\
/// Classes\nxm rather than HKEY_CLASSES_ROOT, so it doesn't need administrator elevation, matching
/// this whole app's own "no installer, no elevation" philosophy. The exact key structure (a bare
/// "URL Protocol" value marking the key as a pluggable protocol handler, a quoted "%1" for the
/// command line) is confirmed against Microsoft's own official documentation for registering a
/// custom URI scheme handler, fetched during Phase 8.3c planning — not guessed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NxmProtocolRegistrar : INxmProtocolRegistrar
{
    private const string KeyPath = @"Software\Classes\nxm";

    public bool IsRegisteredToThisApp()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{KeyPath}\shell\open\command");
        var command = key?.GetValue("") as string;
        // Exact match against Register()'s own written form, not a substring Contains — a loose
        // Contains would false-positive whenever this build's own exe path happens to be a
        // substring of some other registered command (e.g. a sibling install whose folder path
        // contains this one's), reporting "registered to this app" when the registry actually
        // points somewhere else.
        return string.Equals(command, $"\"{ExePath}\" \"%1\"", StringComparison.OrdinalIgnoreCase);
    }

    public void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("", "URL:Nexus Mod Manager Protocol");
        key.SetValue("URL Protocol", "");

        using var commandKey = key.CreateSubKey(@"shell\open\command");
        // The quoted "%1" (not bare %1) is Microsoft's own documented mitigation against a URI
        // containing spaces/quotes/backslashes being split across multiple command-line arguments.
        commandKey.SetValue("", $"\"{ExePath}\" \"%1\"");
    }

    public void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);

    private static string ExePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't determine this app's own executable path.");
}
