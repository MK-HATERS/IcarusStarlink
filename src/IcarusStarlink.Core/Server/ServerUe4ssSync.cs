using System.Security.Cryptography;
using IcarusStarlink.Core.IO;
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
    string Name, bool InGame, bool OnInGame, string? GameVersion, bool OnServer, bool OnOnServer, string? ServerVersion, ServerModState State,
    IReadOnlyList<string> DifferingConfigs)
{
    /// <summary>The game's and the server's copies have different settings (config.lua, config.ini…): <see cref="DifferingConfigs"/> names the files.</summary>
    public bool ConfigDiffers => DifferingConfigs.Count > 0;
}

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
            // No game folder set: nothing is "in the game" (an empty path would read the working folder).
            var inGame = gameModsFolder.Length > 0 && Directory.Exists(game);
            var onServer = Directory.Exists(server);
            var gameVersion = inGame ? Ue4ssModVersion.TryRead(game) : null;
            var serverVersion = onServer ? Ue4ssModVersion.TryRead(server) : null;
            var state = !onServer ? ServerModState.NotOnServer
                : !inGame ? ServerModState.OnlyOnServer
                : CompareCopies(game, server, gameVersion, serverVersion);
            result.Add(new ServerUe4ssComparison(
                name, inGame, inGame && Ue4ssModStart.Starts(gameModsFolder, name, gameTxt), gameVersion,
                onServer, onServer && Ue4ssModStart.Starts(serverModsFolder, name, serverTxt), serverVersion, state,
                inGame && onServer ? ConfigDifferences(game, server) : []));
        }

        return result;
    }

    /// <summary>
    /// The config files (config.lua, config.ini, config\*.ini…) of <paramref name="gameMod"/> that
    /// the server's copy doesn't run the same: different text (line endings aside), or the server
    /// lacks the file. A config-looking file only the server has is the server's own, never a
    /// difference: Pet Commander writes its Invulnerable.ini (the world's tames by id) beside its
    /// config where the world runs, so it's on the server and not in the game (seen live
    /// 2026-10-27: it read as "different settings" that Match settings couldn't copy, since the
    /// game had nothing to copy). The shipped defaults (config.default.lua) are the mod's, not a
    /// setting, and never count. User request 2026-10-27: the Server page said "Same as your game"
    /// while the server ran different settings.
    /// </summary>
    public static IReadOnlyList<string> ConfigDifferences(string gameMod, string serverMod)
    {
        var differing = new List<string>();
        foreach (var config in Ue4ssModConfigFiles.Find(gameMod).Where(c => !c.IsDefaults))
        {
            var a = Path.Combine(gameMod, config.RelativePath);
            var b = Path.Combine(serverMod, config.RelativePath);
            if (!File.Exists(b) || NormalizedText(a) != NormalizedText(b))
            {
                differing.Add(config.RelativePath);
            }
        }

        return differing;
    }

    private static string NormalizedText(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');

    /// <summary>
    /// Makes <paramref name="targetModFolder"/>'s settings the same as <paramref name="sourceModFolder"/>'s:
    /// every config file the source has (its shipped defaults aside) is copied over, and a target
    /// file that is replaced is saved first under <paramref name="backupFolder"/> (the mod's name,
    /// then the time). Config files only the target has are left alone. Returns the files written.
    /// </summary>
    public static IReadOnlyList<string> CopyConfigs(string sourceModFolder, string targetModFolder, string backupFolder)
    {
        if (!Directory.Exists(sourceModFolder))
        {
            throw new DirectoryNotFoundException($"'{sourceModFolder}' isn't there.");
        }

        if (!Directory.Exists(targetModFolder))
        {
            throw new DirectoryNotFoundException($"'{targetModFolder}' isn't there.");
        }

        var written = new List<string>();
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        foreach (var config in Ue4ssModConfigFiles.Find(sourceModFolder).Where(c => !c.IsDefaults))
        {
            var source = Path.Combine(sourceModFolder, config.RelativePath);
            var target = Path.Combine(targetModFolder, config.RelativePath);
            if (File.Exists(target))
            {
                if (File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target)))
                {
                    continue;
                }

                var backup = Path.Combine(backupFolder, Path.GetFileName(Path.TrimEndingDirectorySeparator(targetModFolder)), stamp, config.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicFile.Copy(source, target);
            written.Add(config.RelativePath);
        }

        return written;
    }

    private static IEnumerable<string> ModFolders(string modsFolder) =>
        modsFolder.Length > 0 && Directory.Exists(modsFolder)
            ? Directory.GetDirectories(modsFolder).Select(Path.GetFileName).OfType<string>().Where(n => !IsUpdateLeftover(n))
            : [];

    private static ServerModState CompareCopies(string game, string server, string? gameVersion, string? serverVersion)
    {
        if (gameVersion is not null && serverVersion is not null && TryCompareVersions(gameVersion, serverVersion) is { } order and not 0)
        {
            return order > 0 ? ServerModState.ServerOutOfDate : ServerModState.ServerNewer;
        }

        return ServerHasGameFiles(game, server) ? ServerModState.Same : ServerModState.ServerOutOfDate;
    }

    /// <summary>
    /// The server has every file the game's copy ships, byte for byte (config, logs and the like
    /// aside). Files only on the server are its own (state a mod writes there: players.json,
    /// remembered.txt) and never a difference (review 2026-10-23: they read as "differs" and an
    /// update took them off the live server).
    /// </summary>
    public static bool ServerHasGameFiles(string game, string server)
    {
        var gameFiles = ContentFiles(game);
        var serverLocal = LocalFiles(server);
        foreach (var (relative, gamePath) in gameFiles)
        {
            var serverPath = Path.Combine(server, relative);
            if (serverLocal.Contains(relative) || !File.Exists(serverPath))
            {
                return false;
            }

            if (new FileInfo(gamePath).Length != new FileInfo(serverPath).Length || !HashOf(gamePath).SequenceEqual(HashOf(serverPath)))
            {
                return false;
            }
        }

        return true;
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
            if (IsRuntimeFile(relative) || IsArcFissionLibraryState(modFolder, relative))
            {
                local.Add(relative);
            }
        }

        return local;
    }

    /// <summary>
    /// What ArcFission Library (and the ArcFission mods talking to it) write in its folder while the
    /// game runs (its main.lua 1.3.0 and README): state.lua (what the current data pak was built
    /// from), status.lua (its answer to the mods), registry.txt (each mod's handshake this session),
    /// core_queue.txt and core_running.txt (written each start). They describe the game's own
    /// session, not settings: copied onto a server they're stale there. core_off.txt is the user's
    /// own switch and isn't one of these.
    /// </summary>
    private static readonly string[] ArcFissionLibraryStateFiles = ["state.lua", "status.lua", "registry.txt", "core_queue.txt", "core_running.txt"];

    /// <summary>A file in the ArcFission Library folder (named "ArcFissionLibrary…", as InstallManifestNames finds it) that is its runtime state.</summary>
    private static bool IsArcFissionLibraryState(string modFolder, string relativePath)
    {
        var letters = new string([.. Path.GetFileName(Path.TrimEndingDirectorySeparator(modFolder)).Where(char.IsLetterOrDigit)]);
        return letters.StartsWith("ArcFissionLibrary", StringComparison.OrdinalIgnoreCase)
            && ArcFissionLibraryStateFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Copies a mod folder from the game onto the server, without the game's runtime state of ArcFission Library.</summary>
    private static void CopyModFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (!IsArcFissionLibraryState(source, name))
            {
                File.Copy(file, Path.Combine(target, name), overwrite: true);
            }
        }

        foreach (var folder in Directory.GetDirectories(source))
        {
            Directories.Copy(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    private static readonly string[] RuntimeExtensions = [".log", ".bak", ".cache", ".tmp", ".old", ".txt", ".md"];

    // A folder moved aside mid-update (CopyToServer) isn't a mod.
    private static bool IsUpdateLeftover(string folder) => folder.Contains(".updating-", StringComparison.Ordinal);

    /// <summary>Written by the mod or the user, not shipped code: enabled.txt, logs, caches, backups, notes and state text files.</summary>
    private static bool IsRuntimeFile(string relativePath) =>
        string.Equals(relativePath, Ue4ssModStart.EnabledFileName, StringComparison.OrdinalIgnoreCase)
        || RuntimeExtensions.Contains(Path.GetExtension(relativePath).ToLowerInvariant());

    /// <summary>
    /// Hashes by path, size and time written: the Server page compares on every visit and after
    /// every action, and hashing a merged pak of hundreds of MB each time froze the page (review,
    /// 2026-10-23). A file that changes gets a new size or time, so it's hashed again.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Length, DateTime Written), byte[]> Hashes = new();

    private static byte[] HashOf(string path)
    {
        var info = new FileInfo(path);
        var key = (Path.GetFullPath(path).ToLowerInvariant(), info.Length, info.LastWriteTimeUtc);
        if (Hashes.TryGetValue(key, out var known))
        {
            return known;
        }

        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        if (Hashes.Count > 20_000)
        {
            Hashes.Clear();
        }

        Hashes[key] = hash;
        return hash;
    }

    /// <summary>
    /// Puts the game's copy of a mod on the server. When the server already has it, the server's
    /// own config files, the files the mod wrote there (LocalFiles) and its on/off state are kept, and the old
    /// copy is moved into <paramref name="backupsFolder"/>\removed-ue4ss rather than deleted. A mod new
    /// to the server is turned on. The one way a mod goes onto a local server: nothing replaces the
    /// server's copy by deleting it first. ArcFission Library's runtime state (state.lua, status.lua,
    /// registry.txt…) is the game's own session: never copied; the server's own copies are kept.
    /// </summary>
    public static void CopyToServer(string gameModsFolder, string modName, string installFolder, string backupsFolder)
    {
        ServerModFiles.RequireFolderName(modName);
        var source = Path.Combine(gameModsFolder, modName);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"'{modName}' isn't in the game's UE4SS Mods folder.");
        }

        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(installFolder);
        var target = Path.Combine(serverMods, modName);
        var txtPath = Path.Combine(serverMods, Ue4ssModStart.ListFileName);
        if (!Directory.Exists(target))
        {
            CopyModFolder(source, target);
            var list = Ue4ssModsTxt.Load(txtPath);
            list.Set(modName, true);
            list.Save(txtPath);
            return;
        }

        var wasOn = Ue4ssModStart.Starts(serverMods, modName, Ue4ssModsTxt.Load(txtPath));

        // Kept from the server's copy: its config, what the mod writes while running (logs, state
        // .txt files like remembered.txt, which the game's copy has too), and anything the game's
        // copy doesn't ship at all (players.json, data files a mod writes: review 2026-10-23, those
        // used to be taken off the live server by an update).
        var local = LocalFiles(target);
        var keptFiles = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(target, file))
            .Where(relative => !string.Equals(relative, Ue4ssModStart.EnabledFileName, StringComparison.OrdinalIgnoreCase))
            .Where(relative => local.Contains(relative) || !File.Exists(Path.Combine(source, relative)))
            .Select(relative => (RelativePath: relative, Bytes: File.ReadAllBytes(Path.Combine(target, relative))))
            .ToList();

        // The old copy is moved aside first (a rename, so it can't half-delete), the game's copy goes
        // in, and only then does the old one go to the backups (the same place Remove puts mods). If
        // the copy fails, the old copy is put back as it was.
        var aside = $"{target}.updating-{DateTime.Now:yyyyMMddHHmmss}";
        Directory.Move(target, aside);
        try
        {
            CopyModFolder(source, target);
            foreach (var (relative, bytes) in keptFiles)
            {
                var path = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
        }
        catch
        {
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                Directory.Move(aside, target);
            }
            catch (IOException)
            {
                // Leave the moved-aside copy where it is: the error below names the mod, and nothing is lost.
            }

            throw;
        }

        var backup = Path.Combine(backupsFolder, "removed-ue4ss", $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{modName}_before-update");
        Directories.Copy(aside, backup);
        Directory.Delete(aside, recursive: true);
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
}
