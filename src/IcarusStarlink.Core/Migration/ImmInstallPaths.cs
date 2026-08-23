namespace IcarusStarlink.Core.Migration;

/// <summary>
/// Locates a classic IMM install from one of its own files. A user picks a merged mod list, which
/// can sit in two very different places: inside IMM's own folder (LastMergedMods.txt, right next
/// to Extracted_Mods), or inside the GAME's Paks\mods folder (IMM_Merged_Mod.txt, nowhere near
/// it). Only the first can be auto-resolved — the second needs the user to point at the IMM folder
/// themselves, which is why every method here returns null rather than guessing.
/// </summary>
public static class ImmInstallPaths
{
    public const string ExtractedModsFolderName = "Extracted_Mods";

    /// <summary>Walks up from the picked file looking for an IMM install (a folder holding both Extracted_Mods and ExtractedMods.json). Returns the install root, or null when the file isn't inside one.</summary>
    public static string? FindInstallRoot(string modListFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(modListFilePath));

        // Two levels is enough for every real layout seen: the list either sits in the install root
        // itself, or one folder deeper. Walking further would start matching unrelated folders.
        for (var depth = 0; depth < 3 && directory is not null; depth++)
        {
            if (LooksLikeInstallRoot(directory))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>Whether this folder is a classic IMM install — both markers required, so a folder that merely happens to contain an Extracted_Mods isn't mistaken for one.</summary>
    public static bool LooksLikeInstallRoot(string directory) =>
        Directory.Exists(Path.Combine(directory, ExtractedModsFolderName))
        && File.Exists(Path.Combine(directory, ImmExtractedMods.FileName));

    public static string ExtractedModsFolder(string installRoot) => Path.Combine(installRoot, ExtractedModsFolderName);

    public static string ExtractedModsJson(string installRoot) => Path.Combine(installRoot, ImmExtractedMods.FileName);
}
