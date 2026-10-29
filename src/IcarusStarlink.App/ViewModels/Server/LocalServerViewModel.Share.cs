using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One mod in "Compare a patch": what the patch says, what the game has, what the server has.</summary>
public sealed record PatchCompareRow(string Name, string Kind, string PatchText, string GameText, string ServerText)
{
    /// <summary>The game and the server don't agree, one of them lacks what the patch has, or a copy's settings aren't the patch's: shown in the accent colour.</summary>
    public bool Differs =>
        GameText != ServerText
        || (PatchText != "—" && GameText == "—")
        || GameText.Contains("settings differ", StringComparison.Ordinal)
        || ServerText.Contains("settings differ", StringComparison.Ordinal);
}

/// <summary>
/// Share with players (stage 3 of the share-a-mod-list plan, 2026-10-27): a patch made from what
/// the server runs (its ISL-Merged.txt mod list, its UE4SS mods and their settings) for the host to
/// hand to players; "Apply a patch" imports one on the Library side (Merge &amp; Install does that part)
/// and puts its UE4SS mods and settings on this server; "Compare a patch" lines a patch up
/// against the game and the server.
/// </summary>
public sealed partial class LocalServerViewModel
{
    private ILibraryRepository? _library;
    private IPatchService? _patchService;
    private IUe4ssModMetaStore? _ue4ssMetaStore;
    private IPatchImporter? _patchImporter;

    [ObservableProperty]
    private string? _shareStatusMessage;

    /// <summary>Export for players carries the server's UE4SS mod settings (keys, values). On by default: players should run what the server runs.</summary>
    [ObservableProperty]
    private bool _serverExportIncludesConfigs = true;

    public ObservableCollection<PatchCompareRow> PatchCompareRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPatchCompare))]
    private string? _patchCompareTitle;

    public bool HasPatchCompare => PatchCompareTitle is not null;

    /// <summary>The mod names the server's merged pak carries (its ISL-Merged.txt beside it), in merge order; empty without one.</summary>
    private static IReadOnlyList<string> InstalledModNames(string paksModsFolder)
    {
        var manifest = Path.Combine(paksModsFolder, InstallManifestNames.PakManifest);
        try
        {
            return File.Exists(manifest) ? ModListText.ParseNames(File.ReadAllText(manifest)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string? GamePaksModsFolder() =>
        _settings?.Current.IcarusContentPath is { Length: > 0 } content ? Path.Combine(content, "Paks", "mods") : null;

    /// <summary>
    /// A patch of what this server runs: the mods its merged pak lists (the Library copies of
    /// them, by name; one the Library doesn't have can't travel and is named), and the UE4SS mods
    /// on on the server with their settings. Gameplay options aren't recorded on a server, so the
    /// patch carries the defaults; the host's own Merge &amp; Install export has them.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ExportServerPatchAsync()
    {
        if (SelectedProfile is not { } p)
        {
            return;
        }

        if (_library is null || _patchService is null)
        {
            ShareStatusMessage = "Sharing isn't available here.";
            return;
        }

        try
        {
            var names = InstalledModNames(DedicatedServerLayout.PakModsFolder(p.InstallFolder));
            var match = ModListMatcher.Match(names, _library.GetAll());
            var ue4ss = PatchBuilder.Ue4ssModsOnIn(DedicatedServerLayout.Ue4ssModsFolder(p.InstallFolder));
            if (match.Matched.Count == 0 && ue4ss.Count == 0)
            {
                ShareStatusMessage = names.Count > 0
                    ? $"None of the server's mods are in your Library ({string.Join(", ", names)}), so there's nothing to put in a patch."
                    : "The server has no merged pak from Starlink and no UE4SS mods on, so there's nothing to put in a patch.";
                return;
            }

            var (manifest, bundle, skipped) = new PatchBuilder(_library, _ue4ssMetaStore)
                .Build(p.Name, match.Matched, new GameplayOptions(), ue4ss, ServerExportIncludesConfigs, new HashSet<(string Name, string Author)>());
            var extension = bundle.IsEmpty ? "json" : "zip";
            if (_dialogService.PickSaveFile(
                    "Export patch for players",
                    extension == "json" ? "IcarusStarlink patch (*.json)|*.json" : "IcarusStarlink patch (*.zip)|*.zip",
                    $"ISL-Patch-{p.Name}.{extension}") is not { } exportPath)
            {
                return;
            }

            await _patchService.ExportAsync(manifest, bundle, exportPath);

            var parts = new List<string> { $"{manifest.Mods.Count} data mod(s)" };
            if (manifest.Ue4ssMods.Count > 0)
            {
                parts.Add($"{manifest.Ue4ssMods.Count} UE4SS mod(s){(manifest.IncludesConfigs ? " with the server's settings" : "")}");
            }

            ShareStatusMessage = $"Exported what '{p.Name}' runs to '{exportPath}' — {string.Join(" and ", parts)}. Players import it on Merge & Install.";
            if (match.Unmatched.Count > 0)
            {
                ShareStatusMessage += $" Not in your Library, so not in the patch: {string.Join(", ", match.Unmatched)}.";
            }

            if (skipped.Count > 0)
            {
                ShareStatusMessage += $" Left out (no source and no pak files to bundle): {string.Join(", ", skipped)}.";
            }

            _activityLog.Log($"Exported a patch of '{p.Name}' to '{exportPath}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ShareStatusMessage = $"Export failed: {ex.Message}";
            _activityLog.Log(ShareStatusMessage, ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// A patch onto this server: Merge &amp; Install imports it on the Library side (mods, downloads,
    /// UE4SS mods and settings, the profile), then each of the patch's UE4SS mods goes on the
    /// server from the game's copy (or from the patch, when it's bundled and the game doesn't
    /// have it), with the patch's settings written into the server's copy. The merged pak is
    /// built on Merge &amp; Install and installed from here, as before. The server must be stopped.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ApplyPatchToServerAsync()
    {
        if (SelectedProfile is not { } p || !EnsureInstallStoppedFor(p, "apply a patch"))
        {
            return;
        }

        if (_patchService is null)
        {
            ShareStatusMessage = "Sharing isn't available here.";
            return;
        }

        if (_dialogService.PickFile("Apply a patch to this server", "IcarusStarlink patch (*.json;*.zip)|*.json;*.zip") is not { } patchPath)
        {
            return;
        }

        try
        {
            using var contents = await _patchService.ImportAsync(patchPath);
            var manifest = contents.Manifest;
            string? libraryLine = null;
            if (_patchImporter is not null)
            {
                libraryLine = await _patchImporter.ImportPatchContentsAsync(contents);
                if (libraryLine is null)
                {
                    // Refused (a build in progress on Merge & Install): the Library side didn't happen, so nothing goes on the server either.
                    ShareStatusMessage = "Merge & Install is busy building; apply the patch again when it's done.";
                    return;
                }

                if (libraryLine.StartsWith("Import failed", StringComparison.Ordinal))
                {
                    ShareStatusMessage = libraryLine;
                    return;
                }
            }

            var serverLines = new List<string>();
            if (manifest.Ue4ssMods.Count > 0)
            {
                if (!ServerHasUe4ss)
                {
                    serverLines.Add("install UE4SS on the server first, then apply the patch again for its UE4SS mods");
                }
                else if (!EnsureInstallStoppedFor(p, "apply a patch"))
                {
                    // The Library import can take a while, and Start stays clickable meanwhile:
                    // looked at again, so mod files are never swapped under a server that started.
                    serverLines.Add("the server was started while the patch was imported, so its UE4SS mods weren't changed; stop it and apply the patch again");
                }
                else
                {
                    serverLines.AddRange(await PutPatchUe4ssOnServerAsync(p, contents));
                }
            }

            serverLines.Add("build and install the merged pak on Merge & Install, then press Update the server's pak here");
            ShareStatusMessage = $"Applied '{manifest.ProfileName}'. {libraryLine ?? ""} Server: {string.Join("; ", serverLines)}.";
            _activityLog.Log($"Applied the patch '{manifest.ProfileName}' to '{p.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ShareStatusMessage = $"Couldn't apply the patch: {ex.Message}";
            _activityLog.Log(ShareStatusMessage, ActivityEntryKind.Warning);
        }

        RefreshMods();
    }

    /// <summary>Each of the patch's UE4SS mods onto the server: the game's copy (CopyToServer keeps the server's own files), else the bundled one; then the patch's settings into the server's copy.</summary>
    private async Task<List<string>> PutPatchUe4ssOnServerAsync(DedicatedServerProfile p, PatchImportContents contents)
    {
        var lines = new List<string>();
        var put = new List<string>();
        var missing = new List<string>();
        var settingsWritten = 0;
        var gameMods = GameModsFolder();
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(p.InstallFolder);
        var backups = ServerBackupsFolder(p);
        BeginBackupWork(p);
        try
        {
            await TryBackupAsync(p, "before-patch");
            foreach (var mod in contents.Manifest.Ue4ssMods)
            {
                var name = mod.Name;
                var target = Path.Combine(serverMods, name);
                try
                {
                    if (gameMods is not null && Directory.Exists(Path.Combine(gameMods, name)))
                    {
                        await OffUiThread(() => ServerUe4ssSync.CopyToServer(gameMods, name, p.InstallFolder, backups));
                        put.Add(name);
                    }
                    else if (contents.BundledUe4ssFolders.TryGetValue(name, out var bundled))
                    {
                        if (!Directory.Exists(target))
                        {
                            await OffUiThread(() => Directories.Copy(bundled, target));
                        }

                        ServerUe4ssSync.SetEnabled(p.InstallFolder, name, true);
                        put.Add(name);
                    }
                    else if (!Directory.Exists(target))
                    {
                        missing.Add(name);
                        continue;
                    }

                    foreach (var config in mod.Configs)
                    {
                        var relative = config.Path.Replace('/', Path.DirectorySeparatorChar);
                        if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                        {
                            continue;
                        }

                        Ue4ssModConfigCarryOver.ApplyValues(target, relative, config.Text, Path.Combine(backups, "ue4ss-configs"));
                        settingsWritten++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lines.Add($"couldn't put {name} on the server ({ex.Message})");
                }
            }
        }
        finally
        {
            EndBackupWork();
        }

        if (put.Count > 0)
        {
            lines.Insert(0, $"{put.Count} UE4SS mod(s) put on the server ({string.Join(", ", put)})");
        }

        if (settingsWritten > 0)
        {
            lines.Add("the patch's settings written into the server's copies (the old files are in its backups folder)");
        }

        if (missing.Count > 0)
        {
            lines.Add($"not on the server and not in your game, so skipped: {string.Join(", ", missing)}");
        }

        return lines;
    }

    /// <summary>A patch against this game and this server, one row per mod: in the patch, installed here, installed there; UE4SS mods on/off and whether their settings match the patch's.</summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ComparePatchAsync()
    {
        if (SelectedProfile is not { } p || _patchService is null)
        {
            return;
        }

        if (_dialogService.PickFile("Compare a patch", "IcarusStarlink patch (*.json;*.zip)|*.json;*.zip") is not { } patchPath)
        {
            return;
        }

        try
        {
            using var contents = await _patchService.ImportAsync(patchPath);
            var manifest = contents.Manifest;
            var gameNames = GamePaksModsFolder() is { } gamePaks ? InstalledModNames(gamePaks) : [];
            var serverNames = InstalledModNames(DedicatedServerLayout.PakModsFolder(p.InstallFolder));
            var library = _library?.GetAll() ?? [];
            var rows = new List<PatchCompareRow>();

            static bool Has(IReadOnlyList<string> names, string name) => names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            // A pak mod is its own pak file(s) in Paks\mods, never a name in the merged pak's list.
            static bool HasPaks(string? paksFolder, PatchModEntry mod) =>
                paksFolder is not null && mod.PakFileNames.Count > 0 && mod.PakFileNames.All(n => File.Exists(Path.Combine(paksFolder, n)));
            var gamePaksFolder = GamePaksModsFolder();
            var serverPaksFolder = DedicatedServerLayout.PakModsFolder(p.InstallFolder);
            foreach (var mod in manifest.Mods)
            {
                var inLibrary = library.Any(e => string.Equals(e.Name, mod.Name, StringComparison.OrdinalIgnoreCase));
                var inGame = mod.IsPak ? HasPaks(gamePaksFolder, mod) : Has(gameNames, mod.Name);
                var onServer = mod.IsPak ? HasPaks(serverPaksFolder, mod) : Has(serverNames, mod.Name);
                rows.Add(new PatchCompareRow(
                    mod.Name, mod.IsPak ? "Pak mod" : "Data mod", $"v{mod.Version}",
                    inGame ? "installed" : inLibrary ? "in Library, not installed" : "—",
                    onServer ? "installed" : "—"));
            }

            foreach (var name in gameNames.Concat(serverNames).Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(n => !manifest.Mods.Any(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase))))
            {
                rows.Add(new PatchCompareRow(name, "Data mod", "—", Has(gameNames, name) ? "installed" : "—", Has(serverNames, name) ? "installed" : "—"));
            }

            var gameMods = GameModsFolder();
            var serverMods = DedicatedServerLayout.Ue4ssModsFolder(p.InstallFolder);
            var gameOn = gameMods is null ? [] : PatchBuilder.Ue4ssModsOnIn(gameMods).Select(m => m.Name).ToList();
            var serverOn = PatchBuilder.Ue4ssModsOnIn(serverMods).Select(m => m.Name).ToList();
            foreach (var mod in manifest.Ue4ssMods)
            {
                rows.Add(new PatchCompareRow(
                    mod.Name, "UE4SS mod", $"on{(mod.Version is { Length: > 0 } v ? $" (v{v})" : "")}{(mod.Configs.Count > 0 ? ", with settings" : "")}",
                    DescribeUe4ssCopy(gameMods is null ? null : Path.Combine(gameMods, mod.Name), Has(gameOn, mod.Name), mod),
                    DescribeUe4ssCopy(Path.Combine(serverMods, mod.Name), Has(serverOn, mod.Name), mod)));
            }

            foreach (var name in gameOn.Concat(serverOn).Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(n => !manifest.Ue4ssMods.Any(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase))))
            {
                rows.Add(new PatchCompareRow(name, "UE4SS mod", "—", Has(gameOn, name) ? "on" : "—", Has(serverOn, name) ? "on" : "—"));
            }

            PatchCompareRows.Clear();
            foreach (var row in rows)
            {
                PatchCompareRows.Add(row);
            }

            var when = manifest.ExportedAtUtc is { } at ? $" exported {at.ToLocalTime():g}" : "";
            var with = manifest.StarlinkVersion is { Length: > 0 } sv ? $" with Starlink {sv}" : "";
            PatchCompareTitle = $"'{manifest.ProfileName}'{when}{with} — {rows.Count(r => r.Differs)} of {rows.Count} row(s) differ";
            ShareStatusMessage = null;
        }
        catch (Exception ex)
        {
            ShareStatusMessage = $"Couldn't read the patch: {ex.Message}";
        }
    }

    /// <summary>"on", "on · settings differ", "off", "—" for one copy of a UE4SS mod against the patch's entry.</summary>
    private static string DescribeUe4ssCopy(string? folder, bool on, PatchUe4ssEntry mod)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return "—";
        }

        var text = on ? "on" : "off";
        var differs = mod.Configs.Any(c =>
        {
            var path = Path.Combine(folder, c.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                return !File.Exists(path) || Normalize(File.ReadAllText(path)) != Normalize(c.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        });
        return differs ? text + " · settings differ" : text;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');

    [RelayCommand]
    private void ClosePatchCompare()
    {
        PatchCompareRows.Clear();
        PatchCompareTitle = null;
    }
}
