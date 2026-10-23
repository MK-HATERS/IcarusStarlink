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

    // Starlink's own key, not a value under nxm: another manager re-registering may delete and
    // rewrite the whole nxm key, and the handler it replaced must survive that.
    private const string MemoryKeyPath = @"Software\IcarusStarlink\Nxm";
    private const string PreviousValueName = "PreviousHandler";

    // One value per other mod manager: name = its command, data = its Nexus game domains ("crimsondesert, …").
    private const string AppsKeyName = "Apps";

    public bool IsRegisteredToThisApp() =>
        // Exact match against Register()'s own written form, not a substring Contains — a loose
        // Contains would false-positive whenever this build's own exe path happens to be a
        // substring of some other registered command (e.g. a sibling install whose folder path
        // contains this one's), reporting "registered to this app" when the registry actually
        // points somewhere else.
        string.Equals(CurrentHandlerCommand(), $"\"{ExePath}\" \"%1\"", StringComparison.OrdinalIgnoreCase);

    public string? CurrentHandlerCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{KeyPath}\shell\open\command");
        return key?.GetValue("") is string { Length: > 0 } command ? command : null;
    }

    public string? PreviousHandlerCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(MemoryKeyPath);
        return key?.GetValue(PreviousValueName) is string { Length: > 0 } command ? command : null;
    }

    public IReadOnlyList<NxmOtherApp> OtherApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{MemoryKeyPath}\{AppsKeyName}");
        return key is null
            ? []
            : [.. key.GetValueNames()
                .Where(command => command.Length > 0)
                .Select(command => new NxmOtherApp(command, NxmHandlerCommand.ParseGames(key.GetValue(command) as string)))];
    }

    public void SetOtherAppGames(string command, string games)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{MemoryKeyPath}\{AppsKeyName}");
        key.SetValue(command, string.Join(", ", NxmHandlerCommand.ParseGames(games)));
    }

    public void RemoveOtherApp(string command)
    {
        using (var apps = Registry.CurrentUser.OpenSubKey($@"{MemoryKeyPath}\{AppsKeyName}", writable: true))
        {
            apps?.DeleteValue(command, throwOnMissingValue: false);
        }

        if (string.Equals(PreviousHandlerCommand(), command, StringComparison.OrdinalIgnoreCase))
        {
            using var memory = Registry.CurrentUser.OpenSubKey(MemoryKeyPath, writable: true);
            memory?.DeleteValue(PreviousValueName, throwOnMissingValue: false);
        }
    }

    public void Register()
    {
        var replaced = CurrentHandlerCommand();
        if (replaced is not null && !NxmHandlerCommand.IsStarlink(replaced))
        {
            using var memory = Registry.CurrentUser.CreateSubKey(MemoryKeyPath);
            memory.SetValue(PreviousValueName, replaced);

            // Every manager seen holding the links joins the routing list (Settings › Nexus), its
            // games left for the user to fill in; until then it gets the games nobody claims.
            using var apps = memory.CreateSubKey(AppsKeyName);
            if (apps.GetValue(replaced) is null)
            {
                apps.SetValue(replaced, "");
            }
        }

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("", "URL:Nexus Mod Manager Protocol");
        key.SetValue("URL Protocol", "");

        using var commandKey = key.CreateSubKey(@"shell\open\command");
        // The quoted "%1" (not bare %1) is Microsoft's own documented mitigation against a URI
        // containing spaces/quotes/backslashes being split across multiple command-line arguments.
        commandKey.SetValue("", $"\"{ExePath}\" \"%1\"");
    }

    /// <summary>Hands nxm:// links back to the manager Register replaced, when it's still installed; otherwise nothing handles them.</summary>
    public void Unregister()
    {
        var previous = PreviousHandlerCommand();
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        if (previous is null || !File.Exists(NxmHandlerCommand.ExePathOf(previous)))
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("", "URL:Nexus Mod Manager Protocol");
        key.SetValue("URL Protocol", "");
        using var commandKey = key.CreateSubKey(@"shell\open\command");
        commandKey.SetValue("", previous);
    }

    private static string ExePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't determine this app's own executable path.");
}
