using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One of the game's own UE4SS mods, offered for the server with a tick box.</summary>
public sealed partial class Ue4ssServerPick(string name, bool enabledInGame, bool onServer) : ObservableObject
{
    public string Name { get; } = name;
    public bool EnabledInGame { get; } = enabledInGame;
    public bool OnServer { get; } = onServer;
    public string Note => OnServer ? "on the server" : EnabledInGame ? "enabled in your game" : "disabled in your game";

    [ObservableProperty]
    private bool _isChecked = enabledInGame && !onServer;
}

/// <summary>
/// Phase 3: mods on the local server. Paks: the merged pak Merge &amp; Install builds goes into the
/// server's Content\Paks\mods (players need the same content mods). UE4SS: verified to load on the
/// dedicated server through dwmapi.dll, so the loader and chosen Lua mods are copied from the game
/// install. Every change needs the server stopped, backs up its worlds first, and never deletes —
/// anything taken off the server is moved into its backups folder.
/// </summary>
public sealed partial class LocalServerViewModel
{
    /// <summary>The framework's own mods — they come with the loader, so they are not offered one by one.</summary>
    private static readonly HashSet<string> Ue4ssFrameworkMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "shared", "Keybinds", "BPML_GenericFunctions", "BPModLoaderMod", "CheatManagerEnablerMod",
        "ConsoleCommandsMod", "ConsoleEnablerMod", "LineTraceMod", "SplitScreenMod",
    };

    public ObservableCollection<ServerPakFile> ServerPaks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedPakCommand))]
    private ServerPakFile? _selectedServerPak;

    public ObservableCollection<ServerUe4ssMod> ServerUe4ssMods { get; } = [];

    public ObservableCollection<Ue4ssServerPick> GameUe4ssMods { get; } = [];

    [ObservableProperty]
    private bool _serverHasUe4ss;

    [ObservableProperty]
    private string _serverUe4ssSummary = "";

    [ObservableProperty]
    private string _mergedPakSummary = "";

    public bool HasMergedPak => _mergedPakPath is not null && File.Exists(_mergedPakPath);

    /// <summary>
    /// Re-checks the merged pak Merge &amp; Install builds. Called when the Server page is shown and
    /// after a build (MergedPakBuiltMessage), not only on profile select — otherwise "Install my
    /// merged pak" stayed disabled after the first build until the app restarted.
    /// </summary>
    public void RefreshMergedPak()
    {
        OnPropertyChanged(nameof(HasMergedPak));
        MergedPakSummary = HasMergedPak
            ? $"Your merged pak: {Path.GetFileName(_mergedPakPath)} — built {File.GetLastWriteTime(_mergedPakPath!):g}"
            : "No merged pak yet — build one on the Merge & Install page first.";
    }

    private void RefreshMods()
    {
        ServerPaks.Clear();
        ServerUe4ssMods.Clear();
        GameUe4ssMods.Clear();
        RefreshMergedPak();
        if (SelectedProfile is not { } p)
        {
            return;
        }

        foreach (var pak in ServerModFiles.ListPaks(p.InstallFolder))
        {
            ServerPaks.Add(pak);
        }

        ServerHasUe4ss = DedicatedServerLayout.IsUe4ssInstalled(p.InstallFolder);
        var logPath = Path.Combine(DedicatedServerLayout.Win64Folder(p.InstallFolder), "ue4ss", "UE4SS.log");
        var version = File.Exists(logPath) ? Ue4ssLogVersionParser.Parse(ReadFirstLines(logPath, 20)) : null;
        ServerUe4ssSummary = ServerHasUe4ss
            ? $"UE4SS is installed on the server{(version is null ? "" : $" (v{version})")}."
            : "UE4SS isn't installed on the server. Lua mods that need to run on the host (StorageLink, Wireless Power…) need it.";

        var onServer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in ServerModFiles.ListUe4ssMods(p.InstallFolder).Where(m => !Ue4ssFrameworkMods.Contains(m.Name)))
        {
            ServerUe4ssMods.Add(mod);
            onServer.Add(mod.Name);
        }

        if (GameModsFolder() is { } gameMods && Directory.Exists(gameMods))
        {
            var gameTxt = Ue4ssModsTxt.Load(Path.Combine(gameMods, "mods.txt"));
            foreach (var folder in Directory.GetDirectories(gameMods).Select(Path.GetFileName).Where(n => n is not null && !Ue4ssFrameworkMods.Contains(n)).Order(StringComparer.OrdinalIgnoreCase))
            {
                var enabled = gameTxt.IsEnabled(folder!) ?? File.Exists(Path.Combine(gameMods, folder!, "enabled.txt"));
                GameUe4ssMods.Add(new Ue4ssServerPick(folder!, enabled, onServer.Contains(folder!)));
            }
        }
    }

    private string? GameModsFolder() =>
        _settings?.Current.IcarusContentPath is { Length: > 0 } content ? Ue4ssGamePaths.ResolveModsFolder(content) : null;

    private string? GameWin64Folder() =>
        _settings?.Current.IcarusContentPath is { Length: > 0 } content ? Ue4ssGamePaths.ResolveWin64Folder(content) : null;

    /// <summary>Mod files are locked while the server runs, and swapping them under a live world is how saves break.</summary>
    private bool EnsureStoppedFor(string action)
    {
        if (Running is null)
        {
            return true;
        }

        StatusMessage = $"Stop the server before you {action}.";
        return false;
    }

    [RelayCommand]
    private void InstallMergedPak()
    {
        if (SelectedProfile is not { } p || !EnsureStoppedFor("change its mods"))
        {
            return;
        }

        if (!HasMergedPak)
        {
            StatusMessage = "No merged pak yet — build one on the Merge & Install page first.";
            return;
        }

        // Older names of the merged pak are replaced by InstallPak, so they aren't "left alone".
        var others = ServerModFiles.ListPaks(p.InstallFolder)
            .Where(f => !string.Equals(f.FileName, Path.GetFileName(_mergedPakPath), StringComparison.OrdinalIgnoreCase))
            .Where(f => !ServerModFiles.LegacyMergedPakFileNames.Contains(f.FileName, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.FileName)
            .ToList();
        var otherNote = others.Count == 0
            ? ""
            : $"\n\nAlso in the server's mods folder (left alone — both load): {string.Join(", ", others)}. Remove an old merged pak of the same mods, or players get both.";
        if (!_dialogService.Confirm(
                $"Copy your merged pak to '{p.Name}'?\n\nIt goes into {DedicatedServerLayout.PakModsFolder(p.InstallFolder)}. Players need the same content mods, so they should install the same build.{otherNote}",
                "Install mods on the server", ThemedConfirmSeverity.Question))
        {
            return;
        }

        try
        {
            TryBackup("before-mods");
            ServerModFiles.InstallPak(_mergedPakPath!, p.InstallFolder, PakIO.InstallManifestNames.PakManifest);
            StatusMessage = $"Installed {Path.GetFileName(_mergedPakPath)} on '{p.Name}'. It loads on the next start.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't copy the pak: {ex.Message}";
        }

        RefreshMods();
        RefreshBackups();
    }

    private bool CanRemoveSelectedPak() => SelectedServerPak is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedPak))]
    private void RemoveSelectedPak()
    {
        if (SelectedProfile is not { } p || SelectedServerPak is not { } pak || !EnsureStoppedFor("change its mods"))
        {
            return;
        }

        if (!_dialogService.Confirm($"Take {pak.FileName} off '{p.Name}'?\n\nIt is moved into this server's backups folder, not deleted.", "Remove pak", ThemedConfirmSeverity.Question))
        {
            return;
        }

        try
        {
            var movedTo = ServerModFiles.RemovePak(p.InstallFolder, pak.FileName, ServerBackupsFolder(p));
            StatusMessage = $"Moved {pak.FileName} to {movedTo}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't remove the pak: {ex.Message}";
        }

        RefreshMods();
    }

    [RelayCommand]
    private void InstallUe4ssOnServer()
    {
        if (SelectedProfile is not { } p || !EnsureStoppedFor("install UE4SS on it"))
        {
            return;
        }

        if (GameWin64Folder() is not { } gameWin64)
        {
            StatusMessage = "Set your Icarus Content folder in Settings first — UE4SS is copied from your game's install.";
            return;
        }

        try
        {
            StatusMessage = ServerModFiles.InstallUe4ssFromGame(gameWin64, p.InstallFolder)
                ? $"UE4SS copied from your game to '{p.Name}' (loader, settings, framework). Tick the Lua mods to put on it below."
                : "Your game has no UE4SS to copy — install it from the Settings page first.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't install UE4SS on the server: {ex.Message}";
        }

        RefreshMods();
    }

    [RelayCommand]
    private void CopyCheckedUe4ssMods()
    {
        if (SelectedProfile is not { } p || !EnsureStoppedFor("change its mods"))
        {
            return;
        }

        var picked = GameUe4ssMods.Where(m => m.IsChecked).ToList();
        if (picked.Count == 0 || GameModsFolder() is not { } gameMods)
        {
            StatusMessage = "Tick the UE4SS mods to put on the server first.";
            return;
        }

        if (!ServerHasUe4ss)
        {
            StatusMessage = "Install UE4SS on the server first.";
            return;
        }

        var failures = new List<string>();
        TryBackup("before-mods");
        foreach (var mod in picked)
        {
            try
            {
                ServerModFiles.CopyUe4ssModToServer(gameMods, mod.Name, p.InstallFolder);
            }
            catch (Exception ex)
            {
                failures.Add($"{mod.Name}: {ex.Message}");
            }
        }

        StatusMessage = failures.Count == 0
            ? $"Copied {picked.Count} UE4SS mod(s) to '{p.Name}': {string.Join(", ", picked.Select(m => m.Name))}. Mods that run on players' games too must be installed by every player."
            : $"Copied {picked.Count - failures.Count} of {picked.Count} — {string.Join("; ", failures)}";
        _activityLog.Log(StatusMessage, failures.Count == 0 ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
        RefreshMods();
        RefreshBackups();
    }

    [RelayCommand]
    private void RemoveServerUe4ssMod(ServerUe4ssMod? mod)
    {
        if (SelectedProfile is not { } p || mod is null || !EnsureStoppedFor("change its mods"))
        {
            return;
        }

        if (!_dialogService.Confirm($"Take {mod.Name} off '{p.Name}'?\n\nThe folder is moved into this server's backups folder, not deleted.", "Remove UE4SS mod", ThemedConfirmSeverity.Question))
        {
            return;
        }

        try
        {
            ServerModFiles.RemoveUe4ssMod(p.InstallFolder, mod.Name, ServerBackupsFolder(p));
            StatusMessage = $"Removed {mod.Name} from '{p.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't remove {mod.Name}: {ex.Message}";
        }

        RefreshMods();
    }

    [RelayCommand]
    private void OpenServerPaksFolder()
    {
        if (SelectedProfile is { } p)
        {
            var folder = DedicatedServerLayout.PakModsFolder(p.InstallFolder);
            Directory.CreateDirectory(folder);
            UrlOpener.TryOpen(folder);
        }
    }

    private static IEnumerable<string> ReadFirstLines(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (lines.Count < count && reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (IOException)
        {
            return [];
        }
    }
}
