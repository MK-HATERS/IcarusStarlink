namespace IcarusStarlink.Core.IO;

/// <summary>Folder helpers shared by every project (the same recursive copy used to be written out three times).</summary>
public static class Directories
{
    /// <summary>
    /// Copies everything under <paramref name="source"/> into <paramref name="target"/>, empty
    /// subfolders included, creating folders as needed and replacing files of the same name.
    /// Nothing already in the target is removed.
    /// </summary>
    public static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var folder in Directory.GetDirectories(source))
        {
            Copy(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    /// <summary>
    /// Deletes the folder and everything in it when it's there. A locked or read-only file doesn't
    /// throw: the folder is left, <paramref name="onFailure"/> is told, and false comes back. For
    /// scratch folders and leftovers, never for a step whose failure must stop what follows.
    /// </summary>
    public static bool TryDelete(string folder, Action<Exception>? onFailure = null)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onFailure?.Invoke(ex);
            return false;
        }
    }
}
