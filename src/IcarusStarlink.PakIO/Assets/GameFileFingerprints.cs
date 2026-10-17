using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>One game file at one game build: each function's code fingerprint and one hash of all its default values.</summary>
public sealed class GameFileFingerprint
{
    public Dictionary<string, string> Functions { get; set; } = new(StringComparer.Ordinal);

    public string DefaultsHash { get; set; } = "";

    public static GameFileFingerprint Of(BlueprintSummary summary) => new()
    {
        Functions = new(summary.Functions, StringComparer.Ordinal),
        DefaultsHash = HashDefaults(summary.Defaults),
    };

    /// <summary>Every default value in name order, as one hash: enough to tell "the game changed its settings".</summary>
    private static string HashDefaults(IReadOnlyDictionary<string, string> defaults)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in defaults.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(value).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}

/// <summary>Every recorded game file at one game build, and when that build was first recorded.</summary>
public sealed class GameBuildFingerprints
{
    public DateTimeOffset RecordedAtUtc { get; set; }

    public Dictionary<string, GameFileFingerprint> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// What the game itself changed in one of its files between two recorded builds: functions whose
/// code changed, functions it added or removed, and whether any default value changed. The
/// previous build's function fingerprints are kept, so a mod's copy can be told to still hold the
/// game's old code.
/// </summary>
public sealed record GameFileChange(
    string PreviousBuild, string CurrentBuild,
    IReadOnlyList<string> ChangedFunctions, IReadOnlyList<string> AddedFunctions, IReadOnlyList<string> RemovedFunctions,
    bool SettingsChanged, IReadOnlyDictionary<string, string> PreviousFunctions)
{
    public bool IsEmpty => ChangedFunctions.Count == 0 && AddedFunctions.Count == 0 && RemovedFunctions.Count == 0 && !SettingsChanged;

    /// <summary>"the game changed GetCurrentProspectLocation, added OnLanded and changed its settings in this update".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (ChangedFunctions.Count > 0)
        {
            parts.Add("changed " + Names(ChangedFunctions));
        }

        if (AddedFunctions.Count > 0)
        {
            parts.Add("added " + Names(AddedFunctions));
        }

        if (RemovedFunctions.Count > 0)
        {
            parts.Add("removed " + Names(RemovedFunctions));
        }

        if (SettingsChanged)
        {
            parts.Add("changed its settings");
        }

        return parts.Count == 0 ? "" : "the game " + Join(parts) + " in this update";
    }

    /// <summary>
    /// Proof that a mod's copy of this file was made for the game before this update, or null when
    /// there's none. Proof is: a function the game changed in this update that the mod's copy
    /// still has exactly as the game had it before (the game's old code), or a function the game
    /// added in this update that the mod's copy doesn't have. A function that differs from both
    /// the old and the new game's could be the mod's own change, so it proves nothing.
    /// </summary>
    public string? OutOfDateProof(BlueprintSummary modCopy)
    {
        var stillOld = ChangedFunctions
            .Where(f => modCopy.Functions.TryGetValue(f, out var mine) && PreviousFunctions.TryGetValue(f, out var old) && mine == old)
            .ToList();
        var missing = AddedFunctions.Where(f => !modCopy.Functions.ContainsKey(f)).ToList();
        var reasons = new List<string>();
        if (stillOld.Count > 0)
        {
            reasons.Add($"the game changed {Names(stillOld)} in this update; this mod still has its own older copy");
        }

        if (missing.Count > 0)
        {
            reasons.Add($"the game added {Names(missing)} in this update; this mod's copy doesn't have {(missing.Count == 1 ? "it" : "them")}");
        }

        return reasons.Count == 0 ? null : "out of date: " + string.Join("; ", reasons);
    }

    /// <summary>"A", "A and B", "A, B and 3 more".</summary>
    private static string Names(IReadOnlyList<string> names)
    {
        if (names.Count <= 3)
        {
            return Join(names);
        }

        return $"{string.Join(", ", names.Take(2))} and {names.Count - 2} more";
    }

    private static string Join(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
}

/// <summary>
/// The game's own blueprints, fingerprinted per game build (Cache\game_file_fingerprints.json):
/// only the files Library mods replace with Medium or High risk, each recorded once per build.
/// Two recorded builds are what prove the game changed a function a mod's copy still has the
/// old version of. Safe to use from several threads.
/// </summary>
public sealed class GameFileFingerprintStore
{
    /// <summary>Builds kept; the oldest go first. A few covers "this update" and the one before.</summary>
    public const int MaxBuilds = 6;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, GameBuildFingerprints> _builds;
    private bool _dirty;

    private GameFileFingerprintStore(string path, Dictionary<string, GameBuildFingerprints> builds)
    {
        _path = path;
        _builds = builds;
    }

    /// <summary>Reads the store; a missing or unreadable file starts empty.</summary>
    public static GameFileFingerprintStore Load(string path)
    {
        var builds = new Dictionary<string, GameBuildFingerprints>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, GameBuildFingerprints>>(File.ReadAllText(path), JsonOptions) is { } read)
            {
                foreach (var (build, files) in read)
                {
                    // Read back with the comparers a fresh store has (JSON doesn't keep them).
                    builds[build] = new GameBuildFingerprints
                    {
                        RecordedAtUtc = files.RecordedAtUtc,
                        Files = new(files.Files ?? [], StringComparer.OrdinalIgnoreCase),
                    };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Starts over: the fingerprints are a cache of the game's own files.
        }

        return new GameFileFingerprintStore(path, builds);
    }

    /// <summary>Recorded builds, newest first.</summary>
    public IReadOnlyList<string> Builds
    {
        get
        {
            lock (_lock)
            {
                return [.. _builds.OrderByDescending(b => b.Value.RecordedAtUtc).Select(b => b.Key)];
            }
        }
    }

    /// <summary>Whether this file is recorded for this build.</summary>
    public bool Has(string build, string file)
    {
        lock (_lock)
        {
            return _builds.TryGetValue(build, out var entry) && entry.Files.ContainsKey(Normalize(file));
        }
    }

    /// <summary>Records the game's copy of a file at this build, unless it's recorded already. True when it was new.</summary>
    public bool Record(string build, string file, BlueprintSummary gameCopy, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            if (!_builds.TryGetValue(build, out var entry))
            {
                _builds[build] = entry = new GameBuildFingerprints { RecordedAtUtc = nowUtc };
                Prune(build);
            }

            var key = Normalize(file);
            if (entry.Files.ContainsKey(key))
            {
                return false;
            }

            entry.Files[key] = GameFileFingerprint.Of(gameCopy);
            _dirty = true;
            return true;
        }
    }

    /// <summary>
    /// What the game changed in this file at this build, against the newest other recorded build
    /// that has it. Null when there's no such build (only one recorded so far) or the file isn't
    /// recorded for this build: nothing can be said then.
    /// </summary>
    public GameFileChange? ChangeAt(string currentBuild, string file)
    {
        var key = Normalize(file);
        lock (_lock)
        {
            if (!_builds.TryGetValue(currentBuild, out var current) || !current.Files.TryGetValue(key, out var now))
            {
                return null;
            }

            var previous = _builds
                .Where(b => !string.Equals(b.Key, currentBuild, StringComparison.OrdinalIgnoreCase) && b.Value.Files.ContainsKey(key))
                .OrderByDescending(b => b.Value.RecordedAtUtc)
                .Select(b => (Build: b.Key, File: b.Value.Files[key]))
                .FirstOrDefault();
            if (previous.File is not { } before)
            {
                return null;
            }

            var changed = now.Functions.Keys.Where(f => before.Functions.TryGetValue(f, out var old) && old != now.Functions[f]).Order(StringComparer.Ordinal).ToList();
            var added = now.Functions.Keys.Except(before.Functions.Keys).Order(StringComparer.Ordinal).ToList();
            var removed = before.Functions.Keys.Except(now.Functions.Keys).Order(StringComparer.Ordinal).ToList();
            return new GameFileChange(previous.Build, currentBuild, changed, added, removed,
                !string.Equals(before.DefaultsHash, now.DefaultsHash, StringComparison.Ordinal), new Dictionary<string, string>(before.Functions));
        }
    }

    /// <summary>Writes the store when anything was recorded since the last save.</summary>
    public void SaveIfChanged()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty)
            {
                return;
            }

            json = JsonSerializer.Serialize(_builds, JsonOptions);
            _dirty = false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>Keeps the newest MaxBuilds builds (by when each was first recorded), never the one just added.</summary>
    private void Prune(string keep)
    {
        foreach (var stale in _builds
                     .Where(b => !string.Equals(b.Key, keep, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(b => b.Value.RecordedAtUtc)
                     .Skip(MaxBuilds - 1)
                     .Select(b => b.Key)
                     .ToList())
        {
            _builds.Remove(stale);
        }
    }

    /// <summary>"BP/Player/BP_X.uasset" and "BP\Player\BP_X" are one file.</summary>
    private static string Normalize(string file)
    {
        var path = file.Replace('\\', '/');
        return path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? path[..^".uasset".Length] : path;
    }
}
