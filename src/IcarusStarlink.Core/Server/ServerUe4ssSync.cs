using System.Security.Cryptography;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Server;

/// <summary>How a UE4SS mod on the server compares with the same mod in the game.</summary>
public enum ServerModState
{
    /// <summary>The server has the same files as the game (config files aside).</summary>
    Same,

    /// <summary>The game has a newer version (main.lua's VERSION), or different files with no version to tell.</summary>
    ServerOutOfDate,

    /// <summary>The server's main.lua states a newer version than the game's.</summary>
    ServerNewer,

    /// <summary>In the game, not on the server.</summary>
    NotOnServer,

    /// <summary>On the server, not in the game.</summary>
    OnlyOnServer,
}

/// <summary>One mod in the server's UE4SS table: what the game has, what the server has, how they compare.</summary>
public sealed record ServerUe4ssComparison(
    string Name, bool InGame, bool OnInGame, string? GameVersion, bool OnServer, bool OnOnServer, string? ServerVersion, ServerModState State);

/// <summary>
/// Keeps a dedicated server's UE4SS mods in step with the game's (user request 2026-10-23: the
/// Server page showed the same mods twice and never said a server copy was out of date — a mod
/// updated in the game stayed old on the server). Compares by the version main.lua states, then
/// by file contents; the server's own config files, enabled.txt and logs never count as a
/// difference, and an update keeps them.
/// </summary>
public static class ServerUe4ssSync
{
    public static IReadOnlyList<ServerUe4ssComparison> Compare(string gameModsFolder, string serverModsFolder, IReadOnlySet<string> skip)
    {
        var gameTxt = Ue4ssModsTxt.Load(Path.Combine(gameModsFolder, Ue4ssModStart.ListFileName));
        var serverTxt = Ue4ssModsTxt.Load(Path.Combine(serverModsFolder, Ue4ssModStart.ListFileName));
        var names = ModFolders(gameModsFolder).Concat(ModFolders(serverModsFolder))
            .Where(n => !skip.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        var result = new List<ServerUe4ssComparison>();
        foreach (var name in names)
        {
            var game = Path.Combine(gameModsFolder, name);
            var server = Path.Combine(serverModsFolder, name);
            var inGame = Directory.Exists(game);
            var onServer = Directory.Exists(server);
            var gameVersion = inGame ? Ue4ssModVersion.TryRead(game) : null;
            var serverVersion = onServer ? Ue4ssModVersion.TryRead(server) : null;
            var state = !onServer ? ServerModState.NotOnServer
                : !inGame ? ServerModState.OnlyOnServer
                : CompareCopies(game, server, gameVersion, serverVersion);
            result.Add(new ServerUe4ssComparison(
                name, inGame, inGame && Ue4ssModStart.Starts(gameModsFolder, name, gameTxt), gameVersion,
                onServer, onServer && Ue4ssModStart.Starts(serverModsFolder, name, serverTxt), serverVersion, state));
        }

        return result;
    }

    private static IEnumerable<string> ModFolders(string modsFolder) =>
        Directory.Exists(modsFolder) ? Directory.GetDirectories(modsFolder).Select(Path.GetFileName).OfType<string>() : [];

    private static ServerModState CompareCopies(string game, string server, string? gameVersion, string? serverVersion)
    {
        if (gameVersion is not null && serverVersion is not null && TryCompareVersions(gameVersion, serverVersion) is { } order and not 0)
        {
            return order > 0 ? ServerModState.ServerOutOfDate : ServerModState.ServerNewer;
        }

        return SameContent(game, server) ? ServerModState.Same : ServerModState.ServerOutOfDate;
    }

    /// <summary>Dotted numbers ("1.9.0" vs "1.10.0"); null when either isn't one.</summary>
    private static int? TryCompareVersions(string a, string b)
    {
        static Version? Parse(string s) => Version.TryParse(s.Split('-', '+')[0], out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0)) : null;
        return Parse(a) is { } x && Parse(b) is { } y ? x.CompareTo(y) : null;
    }

    /// <summary>The mod's own files (not its config, enabled.txt or logs) are byte-for-byte the same in both folders.</summary>
    public static bool SameContent(string modA, string modB)
    {
        var a = ContentFiles(modA);
        var b = ContentFiles(modB);
        if (a.Count != b.Count || !a.Keys.All(b.ContainsKey))
        {
            return false;
        }

        foreach (var (relative, pathA) in a)
        {
            var pathB = b[relative];
            if (new FileInfo(pathA).Length != new FileInfo(pathB).Length || !HashOf(pathA).SequenceEqual(HashOf(pathB)))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> ContentFiles(string modFolder)
    {
        var local = LocalFiles(modFolder);
        return Directory.EnumerateFiles(modFolder, "*", SearchOption.AllDirectories)
            .Select(f => (Relative: Path.GetRelativePath(modFolder, f), Full: f))
            .Where(f => !local.Contains(f.Relative))
            .ToDictionary(f => f.Relative, f => f.Full, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The files that belong to one copy, not to the mod: its config, and what it writes while it
    /// runs (logs, caches, backups, its remembered.txt — seen on the user's server 2026-10-23, where
    /// these alone made five identical mods read as different). Never a difference; kept on update.
    /// </summary>
    private static HashSet<string> LocalFiles(string modFolder)
    {
        var local = Ue4ssModConfigFiles.Find(modFolder).Select(c => c.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(modFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(modFolder, file);
            if (IsRuntimeFile(relative))
            {
                local.Add(relative);
            }
        }

        return local;
    }

    private static readonly string[] RuntimeExtensions = [".log", ".bak", ".cache", ".tmp", ".old", ".txt", ".md"];

    /// <summary>Written by the mod or the user, not shipped code: enabled.txt, logs, caches, backups, notes and state text files.</summary>
    private static bool IsRuntimeFile(string relativePath) =>
        string.Equals(relativePath, Ue4ssModStart.EnabledFileName, StringComparison.OrdinalIgnoreCase)
        || RuntimeExtensions.Contains(Path.GetExtension(relativePath).ToLowerInvariant());

    private static byte[] HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    /// <summary>
    /// Puts the game's copy of a mod on the server. When the server already has it, the server's
    /// own config files, the files the mod wrote there (LocalFiles) and its on/off state are kept, and the old
    /// copy is moved into <paramref name="backupsFolder"/>\removed-ue4ss rather than deleted. A mod new
    /// to the server is turned on.
    /// </summary>
    public static void CopyToServer(string gameModsFolder, string modName, string installFolder, string backupsFolder)
    {
        var source = Path.Combine(gameModsFolder, modName);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"'{modName}' isn't in the game's UE4SS Mods folder.");
        }

        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(installFolder);
        var target = Path.Combine(serverMods, modName);
        if (!Directory.Exists(target))
        {
            ServerModFiles.CopyUe4ssModToServer(gameModsFolder, modName, installFolder);
            return;
        }

        var txtPath = Path.Combine(serverMods, Ue4ssModStart.ListFileName);
        var wasOn = Ue4ssModStart.Starts(serverMods, modName, Ue4ssModsTxt.Load(txtPath));
        // The server's config and what the mod wrote there (remembered.txt, caches) stay the server's.
        var keptFiles = LocalFiles(target)
            .Where(relative => !string.Equals(relative, Ue4ssModStart.EnabledFileName, StringComparison.OrdinalIgnoreCase))
            .Select(relative => (RelativePath: relative, Bytes: File.ReadAllBytes(Path.Combine(target, relative))))
            .ToList();

        // The old copy goes to the backups (the same place Remove puts mods), then the game's copy in.
        var backup = Path.Combine(backupsFolder, "removed-ue4ss", $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{modName}_before-update");
        CopyDirectory(target, backup);
        Directory.Delete(target, recursive: true);
        CopyDirectory(source, target);

        foreach (var (relative, bytes) in keptFiles)
        {
            var path = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        SetEnabled(installFolder, modName, wasOn);
    }

    /// <summary>Turns a mod on or off on the server the way UE4SS reads it: mods.txt's line, and enabled.txt (which starts a mod even where mods.txt says 0) removed when off.</summary>
    public static void SetEnabled(string installFolder, string modName, bool enabled)
    {
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(installFolder);
        var folder = Path.Combine(serverMods, modName);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"'{modName}' isn't on the server.");
        }

        var txtPath = Path.Combine(serverMods, Ue4ssModStart.ListFileName);
        var txt = Ue4ssModsTxt.Load(txtPath);
        txt.Set(modName, enabled);
        txt.Save(txtPath);

        var enabledFile = Path.Combine(folder, Ue4ssModStart.EnabledFileName);
        if (!enabled && File.Exists(enabledFile))
        {
            File.Delete(enabledFile);
        }
    }

    /// <summary>The server's copy of a pak is the same file as <paramref name="localPak"/> (size, then contents).</summary>
    public static bool SamePak(string localPak, string serverPak) =>
        File.Exists(localPak) && File.Exists(serverPak)
        && new FileInfo(localPak).Length == new FileInfo(serverPak).Length
        && HashOf(localPak).SequenceEqual(HashOf(serverPak));

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
