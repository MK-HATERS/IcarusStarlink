using System.Text.Json;
using IcarusStarlink.Core.Profiles;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Profiles;

/// <summary>One JSON file per profile under profilesDirectory, named after the profile itself — simple enough that a user could inspect/back these up by hand, matching the app's broader "your files are yours" storage philosophy.</summary>
public sealed class ProfileStore : IProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _profilesDirectory;
    private readonly ILogger<ProfileStore> _logger;

    public ProfileStore(string profilesDirectory, ILogger<ProfileStore> logger)
    {
        _profilesDirectory = profilesDirectory;
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
            return JsonSerializer.Deserialize<Profile>(json, JsonOptions)
                ?? throw new FormatException($"Profile '{name}' is empty or corrupt.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Profile '{name}' is corrupt.", ex);
        }
    }

    public void Save(Profile profile)
    {
        var path = ResolvePath(profile.Name);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
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
