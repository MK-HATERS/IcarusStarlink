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

    IReadOnlyList<string> ListAssetPaths(string folderName);

    byte[] ReadAssetContent(string folderName, string relativePath);

    /// <summary>Null if the mod has no file named "readme" (any extension).</summary>
    string? ReadReadme(string folderName);
}
