using System.IO;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The game's Content folder (…\Icarus\Icarus\Content, the one holding Data\data.pak), found from a
/// folder the player picked: the game folder or its Icarus folder are common picks, and storing
/// those made Install copy the merged pak where the game never loads it.
/// </summary>
public static class IcarusContentFolder
{
    public static bool HasDataPak(string folder) => File.Exists(Path.Combine(folder, "Data", "data.pak"));

    /// <summary>folder itself, else Icarus\Content or Content inside it, else the folder above a picked Data folder: the first one holding Data\data.pak; null when none does.</summary>
    public static string? Find(string folder)
    {
        if (HasDataPak(folder))
        {
            return folder;
        }

        try
        {
            var full = Path.GetFullPath(folder.Trim());
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            string?[] candidates =
            [
                Path.Combine(trimmed, "Icarus", "Content"),
                Path.Combine(trimmed, "Content"),
                string.Equals(Path.GetFileName(trimmed), "Data", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(trimmed) : null,
            ];
            return candidates.FirstOrDefault(c => c is not null && HasDataPak(c));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            // A typed path that isn't a path at all: not the Content folder.
            return null;
        }
    }
}
