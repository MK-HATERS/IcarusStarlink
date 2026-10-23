using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.App.Tests.Services;

public sealed class ExeRenameMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"ExeRename_{Guid.NewGuid():N}");
    private readonly string _install;
    private readonly string _desktop;
    private readonly string _startMenu;
    private readonly FakeShortcuts _shortcuts = new();
    private readonly FakeNxm _nxm = new();

    public ExeRenameMigrationTests()
    {
        _install = Path.Combine(_root, "Starlink");
        _desktop = Path.Combine(_root, "Desktop");
        _startMenu = Path.Combine(_root, "Start");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_desktop);
        Directory.CreateDirectory(Path.Combine(_startMenu, "Games"));
        File.WriteAllText(Path.Combine(_install, "IcarusStarlink.exe"), "app");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ExeRenameMigration Migration() =>
        new(_install, _shortcuts, _nxm, [new(_desktop, false), new(_startMenu, true)]) { LauncherExitWait = TimeSpan.Zero };

    private string OldExe => Path.Combine(_install, "IcarusStarlink.App.exe");

    private string NewExe => Path.Combine(_install, "IcarusStarlink.exe");

    private string Shortcut(string folder, string name, string target)
    {
        var path = Path.Combine(folder, name + ".lnk");
        File.WriteAllText(path, "");
        _shortcuts.Targets[path] = target;
        return path;
    }

    [Fact]
    public void Plan_FindsShortcutsToTheOldExe_InSubfoldersOfRecursiveFolders_AndNothingElse()
    {
        File.WriteAllText(OldExe, "launcher");
        var desktop = Shortcut(_desktop, "Starlink", OldExe);
        var start = Shortcut(Path.Combine(_startMenu, "Games"), "Starlink", OldExe.ToUpperInvariant());
        Shortcut(_desktop, "Other", @"C:\Games\Other.exe");
        Shortcut(_desktop, "Already new", NewExe);
        _nxm.Command = $"\"{OldExe}\" \"%1\"";

        var plan = Migration().Plan();

        Assert.Equal([desktop, start], plan.Shortcuts.Order());
        Assert.True(plan.NxmUsesOldExe);
        Assert.True(plan.OldExeExists);
    }

    [Fact]
    public void Run_RepointsShortcuts_MovesNxm_ThenDeletesTheOldExe()
    {
        File.WriteAllText(OldExe, "launcher");
        var link = Shortcut(_desktop, "Starlink", OldExe);
        _nxm.Command = $"\"{OldExe}\" \"%1\"";
        var migration = Migration();

        var steps = migration.Run(migration.Plan());

        Assert.All(steps, step => Assert.True(step.Succeeded, step.Text));
        Assert.Equal(NewExe, _shortcuts.Targets[link]);
        Assert.Equal(1, _nxm.RegisterCalls);
        Assert.False(File.Exists(OldExe));
        Assert.False(migration.Plan().HasWork);
    }

    [Fact]
    public void Run_KeepsTheOldExe_WhenAShortcutCouldNotBeChanged()
    {
        // Deleting it then would break that shortcut; it's all tried again at the next start.
        File.WriteAllText(OldExe, "launcher");
        var link = Shortcut(_desktop, "Starlink", OldExe);
        _shortcuts.Locked.Add(link);
        var migration = Migration();

        var steps = migration.Run(migration.Plan());

        Assert.Contains(steps, step => !step.Succeeded && step.Text.Contains("Starlink"));
        Assert.True(File.Exists(OldExe));
    }

    [Fact]
    public void Plan_HasNoWork_OnAnInstallThatNeverHadTheOldName()
    {
        _nxm.Command = $"\"{NewExe}\" \"%1\"";

        Assert.False(Migration().Plan().HasWork);
    }

    [Fact]
    public void Run_FailsTheDeleteStep_WhenTheOldExeCannotBeRemoved()
    {
        File.WriteAllText(OldExe, "launcher");
        var migration = Migration();
        using var held = new FileStream(OldExe, FileMode.Open, FileAccess.Read, FileShare.None);

        var steps = migration.Run(migration.Plan());

        Assert.False(Assert.Single(steps).Succeeded);
    }

    [Fact]
    public void IsStarlink_CountsBothExeNames()
    {
        Assert.True(NxmHandlerCommand.IsStarlink($"\"{NewExe}\" \"%1\""));
        Assert.True(NxmHandlerCommand.IsStarlink($"\"{OldExe}\" \"%1\""));
        Assert.False(NxmHandlerCommand.IsStarlink("\"C:\\Vortex\\Vortex.exe\" -d \"%1\""));
    }

    private sealed class FakeShortcuts : IShortcutEditor
    {
        public Dictionary<string, string> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? TryReadTarget(string shortcutPath) => Targets.GetValueOrDefault(shortcutPath);

        public void Repoint(string shortcutPath, string oldTarget, string newTarget)
        {
            if (Locked.Contains(shortcutPath))
            {
                throw new UnauthorizedAccessException("Access is denied.");
            }

            Targets[shortcutPath] = newTarget;
        }
    }

    private sealed class FakeNxm : INxmProtocolRegistrar
    {
        public string? Command { get; set; }

        public int RegisterCalls { get; private set; }

        // The test host isn't IcarusStarlink.exe, so "this app" is the new exe by stand-in.
        public bool IsRegisteredToThisApp() => Command?.Contains("IcarusStarlink.exe", StringComparison.OrdinalIgnoreCase) == true;

        public string? CurrentHandlerCommand() => Command;

        public string? PreviousHandlerCommand() => null;

        public void Register()
        {
            RegisterCalls++;
            Command = Command?.Replace("IcarusStarlink.App.exe", "IcarusStarlink.exe", StringComparison.OrdinalIgnoreCase);
        }

        public void Unregister() => Command = null;
    }
}
