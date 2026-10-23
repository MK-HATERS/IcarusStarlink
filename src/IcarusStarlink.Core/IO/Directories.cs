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
}
