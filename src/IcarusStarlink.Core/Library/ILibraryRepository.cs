namespace IcarusStarlink.Core.Library;

public interface ILibraryRepository
{
    IReadOnlyList<LibraryEntry> GetAll();

    /// <summary>Empty/whitespace query returns everything, matching GetAll().</summary>
    IReadOnlyList<LibraryEntry> Search(string query);

    /// <summary>sourcePath is either a loose mod folder or an .EXMODZ file.</summary>
    LibraryEntry Import(string sourcePath);

    /// <summary>Imports a prebuilt .pak as an opaque library entry — see LibraryEntry.IsOpaquePak.</summary>
    LibraryEntry ImportPak(string pakFilePath);

    /// <summary>Re-scans Extracted_Mods from disk — for mods added/edited outside the app while it was running.</summary>
    void Refresh();

    void Delete(string folderName);

    void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes);

    /// <summary>Sets the ✎ "locally edited" flag — called once by the EXMOD editor's own Save action, never toggled directly by the user the way Pin/Favorite/Notes are.</summary>
    void MarkLocallyEdited(string folderName);

    /// <summary>
    /// Creates a genuinely new mod — an empty EXMOD (no Rows yet) under a fresh Extracted_Mods
    /// folder — for the "New mod…" action, which doesn't read from an existing folder/zip the way
    /// every other Import does. name becomes both the display Name and (sanitized) the EXMOD's own
    /// FileName/folder name; author is required (an empty EXMOD still needs valid header fields).
    /// </summary>
    LibraryEntry CreateBlankMod(string name, string author);

    IReadOnlyList<string> ListAssetPaths(string folderName);

    byte[] ReadAssetContent(string folderName, string relativePath);

    /// <summary>Null if the mod has no file named "readme" (any extension).</summary>
    string? ReadReadme(string folderName);

    /// <summary>
    /// The mod's own folder under Extracted_Mods, for callers (Merge & Install's Rebuild
    /// pipeline) that need to read its full .EXMOD themselves via IcarusStarlink.PakIO directly —
    /// this interface lives in Core, which PakIO itself depends on, so it can't return a PakIO
    /// type without a circular reference. Throws if folderName doesn't exist.
    /// </summary>
    string GetFolderPath(string folderName);
}
