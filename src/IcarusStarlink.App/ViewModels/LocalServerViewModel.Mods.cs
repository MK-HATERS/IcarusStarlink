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

    /// <summary>Every UE4SS mod in the game or on the server, one row each.</summary>
    public ObservableCollection<ServerUe4ssRow> Ue4ssModRows { get; } = [];

    /// <summary>Mods whose server copy is older than (or differs from) the game's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModsTabHeader), nameof(HasModsToUpdate))]
    private int _modsToUpdateCount;

    /// <summary>The server has an older merged pak than the one Merge &amp; Install last built.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModsTabHeader), nameof(HasModsToUpdate))]
    private bool _isServerPakOutOfDate;

    public bool HasModsToUpdate => ModsTabHeader != "Mods";

    /// <summary>"Mods (3 to update)" on the tab itself, so it shows from any tab.</summary>
    public string ModsTabHeader
    {
        get
        {
            var count = ModsToUpdateCount + (IsServerPakOutOfDate ? 1 : 0);
            return count == 0 ? "Mods" : $"Mods ({count} to update)";
        }
    }

    /// <summary>"Install my merged pak" when the server lacks it, "Update the server's pak" when the server's is older.</summary>
    [ObservableProperty]
    private string _installMergedPakLabel = "Install my merged pak";

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
        IsServerPakOutOfDate = false;
        InstallMergedPakLabel = "Install my merged pak";
        if (!HasMergedPak)
        {
            MergedPakSummary = "No merged pak yet — build one on the Merge & Install page first.";
            return;
        }

        var built = File.GetLastWriteTime(_mergedPakPath!);
        var serverCopy = SelectedProfile is { } p ? Path.Combine(DedicatedServerLayout.PakModsFolder(p.InstallFolder), Path.GetFileName(_mergedPakPath!)) : null;
        if (serverCopy is null || !File.Exists(serverCopy))
        {
            MergedPakSummary = $"Your merged pak ({built:g}) isn't on the server yet.";
            return;
        }

        bool same;
        try
        {
            same = ServerUe4ssSync.SamePak(_mergedPakPath!, serverCopy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by the running server: say what's known rather than guess.
            MergedPakSummary = $"Your merged pak was built {built:g}; the server's copy is from {File.GetLastWriteTime(serverCopy):g}.";
            return;
        }

        IsServerPakOutOfDate = !same;
        InstallMergedPakLabel = same ? "Install my merged pak" : "Update the server's pak";
        MergedPakSummary = same
            ? $"✓ The server has your current merged pak (built {built:g})."
            : $"The server's merged pak is from {File.GetLastWriteTime(serverCopy):g}; yours was built {built:g}. Update it so players match the server.";
    }

    /// <summary>
    /// The Server page was opened: re-reads the server's and the game's mods (a UE4SS mod turned on
    /// in the Library since used to be missing from "your game's UE4SS mods" until another server
    /// was picked) and the merged pak.
    /// </summary>
    public void OnPageShown() => RefreshMods();

    private void RefreshMods()
    {
        ServerPaks.Clear();
        Ue4ssModRows.Clear();
        ModsToUpdateCount = 0;
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
        var (version, commit) = Ue4ssInstalledBuild.Read(Path.Combine(DedicatedServerLayout.Win64Folder(p.InstallFolder), "ue4ss"));
        var (gameVersion, gameCommit) = GameWin64Folder() is { } gameWin64 ? Ue4ssInstalledBuild.Read(Path.Combine(gameWin64, "ue4ss")) : (null, null);
        // The server and the game should run the same build: C++ mods are tied to one, and a Lua
        // mod tested in the game should meet the same UE4SS on the server.
        var differs = ServerHasUe4ss && commit is not null && gameCommit is not null && !Ue4ssBuild.SameCommit(commit, gameCommit);
        ServerUe4ssSummary = !ServerHasUe4ss
            ? "UE4SS isn't installed on the server. Lua mods that need to run on the host (StorageLink, Wireless Power…) need it."
            : differs
                ? $"The server has UE4SS {Ue4ssBuild.Describe(version, commit)}, your game has {Ue4ssBuild.Describe(gameVersion, gameCommit)}. " +
                  "Press Install UE4SS from my game to copy your game's build over (the server's settings and mods are kept)."
                : $"UE4SS is installed on the server{(version is null ? "" : $" ({Ue4ssBuild.Describe(version, commit)})")}.";

        try
        {
            var comparisons = ServerUe4ssSync.Compare(GameModsFolder() ?? "", DedicatedServerLayout.Ue4ssModsFolder(p.InstallFolder), Ue4ssFrameworkMods);
            foreach (var comparison in comparisons)
            {
                Ue4ssModRows.Add(new ServerUe4ssRow(comparison, OnServerModToggled));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Couldn't compare the UE4SS mods: {ex.Message}";
        }

        ModsToUpdateCount = Ue4ssModRows.Count(r => r.NeedsUpdate);
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

    /// <summary>A row's Copy to server / Update on server: the game's copy goes on, keeping the server's own config and on/off (ServerUe4ssSync.CopyToServer).</summary>
    [RelayCommand]
    private void CopyUe4ssModToServer(ServerUe4ssRow? row)
    {
        if (row is null)
        {
            return;
        }

        CopyRowsToServer([row]);
    }

    /// <summary>
    /// Make the server match my game: every mod whose server copy is out of date, plus every mod
    /// that's on in the game but missing from the server. Nothing is removed, the server's settings
    /// stay, and its worlds are backed up first. Asks first, naming them.
    /// </summary>
    [RelayCommand]
    private void MatchServerToGame()
    {
        var rows = Ue4ssModRows.Where(r => r.NeedsUpdate || (r.State == ServerModState.NotOnServer && r.Comparison.OnInGame)).ToList();
        if (rows.Count == 0)
        {
            StatusMessage = "The server's UE4SS mods already match your game.";
            return;
        }

        var list = string.Join("\n", rows.Select(r => $"• {r.Name} — {(r.IsOnServer ? "update" : "copy")}"));
        if (!_dialogService.Confirm(
                $"Put your game's copy of these on the server?\n\n{list}\n\nThe server's own config files and on/off settings are kept, old copies go to its backups folder, and nothing else is removed.",
                "Make the server match my game", ThemedConfirmSeverity.Question))
        {
            return;
        }

        CopyRowsToServer(rows);
    }

    private void CopyRowsToServer(IReadOnlyList<ServerUe4ssRow> rows)
    {
        if (SelectedProfile is not { } p || !EnsureStoppedFor("change its mods"))
        {
            return;
        }

        if (!ServerHasUe4ss)
        {
            StatusMessage = "Install UE4SS on the server first.";
            return;
        }

        if (GameModsFolder() is not { } gameMods)
        {
            StatusMessage = "Set your Icarus Content folder in Settings first — mods are copied from your game.";
            return;
        }

        var failures = new List<string>();
        TryBackup("before-mods");
        foreach (var row in rows)
        {
            try
            {
                ServerUe4ssSync.CopyToServer(gameMods, row.Name, p.InstallFolder, ServerBackupsFolder(p));
            }
            catch (Exception ex)
            {
                failures.Add($"{row.Name}: {ex.Message}");
            }
        }

        var names = string.Join(", ", rows.Select(r => r.Name));
        StatusMessage = failures.Count == 0
            ? $"Put your game's copy of {names} on '{p.Name}' (the server's settings were kept). Mods that run on players' games too must be installed by every player."
            : $"Updated {rows.Count - failures.Count} of {rows.Count} — {string.Join("; ", failures)}";
        _activityLog.Log(StatusMessage, failures.Count == 0 ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
        RefreshMods();
        RefreshBackups();
    }

    /// <summary>A row's On switch: turns the mod on or off on the server only (mods.txt / enabled.txt). Needs the server stopped; refused, the switch goes back.</summary>
    private void OnServerModToggled(ServerUe4ssRow row, bool on)
    {
        if (SelectedProfile is not { } p || !EnsureStoppedFor("change its mods"))
        {
            row.ResetServerOn(!on);
            return;
        }

        try
        {
            ServerUe4ssSync.SetEnabled(p.InstallFolder, row.Name, on);
            StatusMessage = $"{row.Name} is {(on ? "on" : "off")} on '{p.Name}' — it takes effect when the server starts.";
        }
        catch (Exception ex)
        {
            row.ResetServerOn(!on);
            StatusMessage = $"Couldn't turn {row.Name} {(on ? "on" : "off")}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveServerUe4ssMod(ServerUe4ssRow? mod)
    {
        if (SelectedProfile is not { } p || mod is null || !mod.IsOnServer || !EnsureStoppedFor("change its mods"))
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
}
