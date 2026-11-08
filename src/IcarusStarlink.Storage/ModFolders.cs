using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.Storage;

/// <summary>
/// The "a mod is a subfolder of one root directory" mechanics shared by the two repositories built
/// on that idea (FolderLibraryRepository over Extracted_Mods, Ue4ssModRepository over
/// Staged_UE4SS). Both had their own byte-identical copies of these, differing only in which root
/// they close over and the wording of the not-found message.
/// </summary>
internal static class ModFolders
{
    /// <summary>
    /// A folder name not already taken under rootDirectory, suffixing _2, _3, … on collision.
    /// additionalNamesToAvoid, if given, is ALSO treated as taken — Ue4ssModRepository passes the
    /// real game Mods folder's own current contents here, so a newly-staged mod can never land
    /// under the same name as an already-installed, unrelated one (which GetAll's own name-keyed
    /// union of staged+installed mods would otherwise silently treat as "the same mod").
    /// desiredName must be one plain folder name (AssetPathGuard.IsSimpleFileName); anything else
    /// throws ArgumentException, because a rooted or nested name would put the new mod outside
    /// rootDirectory.
    /// </summary>
    public static string MakeUnique(string rootDirectory, string desiredName, IReadOnlyCollection<string>? additionalNamesToAvoid = null)
    {
        if (!AssetPathGuard.IsSimpleFileName(desiredName))
        {
            throw new ArgumentException($"'{desiredName}' can't be used as a mod folder name.", nameof(desiredName));
        }

        var candidate = desiredName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(rootDirectory, candidate)) || (additionalNamesToAvoid?.Contains(candidate, StringComparer.OrdinalIgnoreCase) ?? false))
        {
            candidate = $"{desiredName}_{++suffix}";
        }

        return candidate;
    }

    /// <summary>
    /// The full path of an existing mod folder. Throws DirectoryNotFoundException (with
    /// notFoundMessage) rather than returning a path that isn't there, so callers can't act on a
    /// stale name. A name that isn't one plain folder name throws the same way: "" or " " would
    /// otherwise resolve to rootDirectory itself and ".." to its parent, so Delete("") removed
    /// every mod under the root.
    /// </summary>
    public static string Resolve(string rootDirectory, string folderName, string notFoundMessage)
    {
        if (!AssetPathGuard.IsSimpleFileName(folderName))
        {
            throw new DirectoryNotFoundException(notFoundMessage);
        }

        var folder = Path.Combine(rootDirectory, folderName);
        return Directory.Exists(folder) ? folder : throw new DirectoryNotFoundException(notFoundMessage);
    }
}
