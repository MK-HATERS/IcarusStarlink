using System.Text.RegularExpressions;
using IcarusStarlink.Updater;

namespace IcarusStarlink.Updater.Tests;

/// <summary>
/// Uninstall.bat and UpdateApplier.RemoveStaleRootFiles each hold a hand-written list of "the app's
/// own program files" — the updater removes them when a new build no longer ships them, the
/// uninstaller removes them outright. Nothing kept the two in sync (an audit polish item); this
/// test reads the real script so a pattern added to one side fails here until it's on the other.
/// </summary>
public sealed class UninstallScriptParityTests
{
    [Fact]
    public void EveryStaleRootPattern_IsAlsoRemovedByUninstallBat()
    {
        var script = File.ReadAllText(FindUninstallScript());

        // "for %%F in (*.dll *.exe *.pdb *.xml) do" — the glob list the script deletes at the root.
        var globs = Regex.Match(script, @"for %%F in \(([^)]*\*[^)]*)\) do").Groups[1].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(globs);

        foreach (var pattern in UpdateApplier.StaleRootFilePatterns)
        {
            var coveredByGlob = globs.Contains(pattern)
                || (pattern.StartsWith("*.") && globs.Contains("*" + Path.GetExtension(pattern)))
                || (pattern == "createdump.exe" && globs.Contains("*.exe"));
            var namedExplicitly = script.Contains(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);
            Assert.True(coveredByGlob || namedExplicitly, $"Uninstall.bat doesn't remove '{pattern}', but the updater treats it as an app-owned stale file.");
        }

        // The old layout's runtimes\ folder is the one subfolder the updater removes — the script must too.
        Assert.Matches(@"for %%D in \([^)]*\bruntimes\b[^)]*\)", script);
    }

    private static string FindUninstallScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IcarusStarlink.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "IcarusStarlink.App", "Uninstall.bat");
    }
}
