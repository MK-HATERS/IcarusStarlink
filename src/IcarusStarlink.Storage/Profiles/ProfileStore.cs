using System.Text.Json;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO.Install;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Profiles;

/// <summary>One JSON file per profile under profilesDirectory, named after the profile itself — simple enough that a user could inspect/back these up by hand, matching the app's broader "your files are yours" storage philosophy.</summary>
public sealed class ProfileStore : IProfileStore
{
    /// <summary>Deliberately smaller than FolderBackup's own 5-backup default — a profile is small, frequently-saved editor state (not a rare, high-stakes real-game-folder write like a pak/UE4SS-loader install), so a couple of recent copies is enough of a safety net without piling up clutter on every Save.</summary>
    private const int MaxProfileBackups = 3;

    private readonly string _profilesDirectory;
    private readonly string _backupsDirectory;
    private readonly ILogger<ProfileStore> _logger;

    public ProfileStore(string profilesDirectory, string backupsDirectory, ILogger<ProfileStore> logger)
    {
        _profilesDirectory = profilesDirectory;
        _backupsDirectory = backupsDirectory;
        _logger = logger;
        Directory.CreateDirectory(profilesDirectory);
    }

    public IReadOnlyList<string> ProfileNames =>
        // Path.GetFileNameWithoutExtension is only null-annotated for a null input — Directory.GetFiles never returns one.
        [.. Directory.GetFiles(_profilesDirectory, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];

    public Profile Load(string name)
    {
        var path = ResolvePath(name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No profile named '{name}'.");
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Profile>(json, JsonFileStore.Options)
                ?? throw new FormatException($"Profile '{name}' is empty or corrupt.");
        }
        catch (JsonException ex)
        {
            // Unlike every sibling store, a corrupt profile is re-thrown rather than falling back
            // to a default — silently discarding a specific profile the user asked for by name
            // would be a worse surprise than an error message. Still worth logging, the same way
            // the sibling stores log their own fallback-to-default case.
            _logger.LogWarning(ex, "Failed to load profile '{Name}' from {Path}", name, path);
            throw new FormatException($"Profile '{name}' is corrupt.", ex);
        }
    }

    public void Save(Profile profile)
    {
        var path = ResolvePath(profile.Name);

        // Backs up whatever was already saved under this name BEFORE overwriting it — a bad edit
        // (an accidental Clear + Save, a wrong manual conflict pick, MergeInstallViewModel's own
        // new auto-save of an unnamed queue into "Default" right before every Rebuild) is
        // recoverable, not permanent. FolderBackup.BackupFile itself no-ops when path doesn't
        // exist yet (a brand-new profile has nothing to back up), and prunes per-name via the
        // file's own base name, so every profile's backups coexist in one shared directory without
        // needing their own subfolder.
        FolderBackup.BackupFile(path, _backupsDirectory, MaxProfileBackups);

        JsonFileStore.Save(path, profile);
    }

    public void Delete(string name)
    {
        var path = ResolvePath(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Rename(string oldName, string newName)
    {
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) && File.Exists(ResolvePath(newName)))
        {
            throw new InvalidOperationException($"A profile named '{newName}' already exists.");
        }

        var profile = Load(oldName);
        profile.Name = newName;
        Save(profile);

        // Different case of the same name (e.g. "Main" -> "MAIN") lands on the same file path on
        // Windows' case-insensitive filesystem — Save() above already overwrote it correctly, and
        // deleting the "old" path here would just delete the file we all just wrote.
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            Delete(oldName);
        }
    }

    private string ResolvePath(string name)
    {
        var invalidChar = name.IndexOfAny(Path.GetInvalidFileNameChars());
        if (invalidChar >= 0)
        {
            throw new ArgumentException($"Profile name can't contain '{name[invalidChar]}'.", nameof(name));
        }

        return Path.Combine(_profilesDirectory, $"{name}.json");
    }
}
