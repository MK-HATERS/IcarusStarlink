namespace IcarusStarlink.Core.Library;

public interface ILibraryRepository
{
    IReadOnlyList<LibraryEntry> GetAll();

    /// <summary>Folder names under Extracted_Mods the last scan couldn't read (corrupt EXMOD JSON, a locked file) — skipped-and-logged rather than crashing the scan, but surfaced here so the UI can say WHY a folder that's visibly on disk isn't in the Library, instead of that answer living only in a log file.</summary>
    IReadOnlyList<string> UnreadableFolders { get; }

    /// <summary>Empty/whitespace query returns everything, matching GetAll().</summary>
    IReadOnlyList<LibraryEntry> Search(string query);

    /// <summary>sourcePath is either a loose mod folder or an .EXMODZ file. source (e.g. "Nexus"/"Database") is purely informational, stored as LibraryEntry.Source — null for a manual/local import. nexusModId is stored as LibraryEntry.NexusModId, for later update-checking — only meaningful when source is "Nexus".</summary>
    LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null);

    /// <summary>Imports a prebuilt .pak as an opaque library entry — see LibraryEntry.IsOpaquePak. source/nexusModId are the same provenance tags Import(string, string?, int?) takes.</summary>
    LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null);

    /// <summary>
    /// Records real name/author/description/version fetched from the Nexus API for an opaque .pak
    /// entry (which has no embedded metadata of its own to show) — overrides what the detail pane
    /// shows in place of the synthesized "Unknown"/generic description, and gives update-checking
    /// something to compare a later Nexus lookup against. Pass null for any field to leave it
    /// unset. No-op fields aren't cleared by a later call with null — only ever set once, right
    /// after a successful Nexus-sourced Activate.
    /// </summary>
    void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version);

    /// <summary>Re-scans Extracted_Mods from disk — for mods added/edited outside the app while it was running.</summary>
    void Refresh();

    void Delete(string folderName);

    void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes);

    /// <summary>Sets the ✎ "locally edited" flag — called once by the EXMOD editor's own Save action, never toggled directly by the user the way Pin/Favorite/Notes are.</summary>
    void MarkLocallyEdited(string folderName);

    /// <summary>Overrides how this mod's name displays in Library — its real folder name, FileName, and the mod's own file content are never touched. Pass null or an empty/whitespace-only string to clear the override and go back to the default name (the EXMOD's own declared name, or for an opaque pak, its Nexus-enriched name or the pak's filename).</summary>
    void SetDisplayNameOverride(string folderName, string? displayName);

    /// <summary>
    /// Retroactively links an already-imported mod (however it originally got here — a manual
    /// Import folder/EXMODZ/.pak, or an unrecognized entry from a bulk import) to a real Nexus mod
    /// ID, setting Source to "Nexus" so it participates in update-checking exactly like a mod
    /// activated through the real nxm:// pipeline. Unlike SetNexusMetadata (display-field
    /// enrichment for an opaque pak only, set once at Activate time), this only ever sets the ID
    /// and Source — a caller that also wants real name/author/version from the API calls
    /// SetNexusMetadata separately afterward, same as Activate's own two-step flow.
    /// </summary>
    void LinkToNexus(string folderName, int nexusModId);

    /// <summary>Snapshots this mod's whole folder before a risky edit — a real point-in-time backup, independent of the EXMOD editor's own transient per-field preview. Keeps the last 5. Returns the backup's own path.</summary>
    string BackupMod(string folderName);

    /// <summary>Whether at least one backup exists for this mod — for a UI to gray out Restore when there's nothing to restore from.</summary>
    bool HasModBackup(string folderName);

    /// <summary>Replaces this mod's current folder content with its own most recent backup (a real point-in-time restore — the folder is deleted first, not merged with what's there). Returns false if no backup exists yet.</summary>
    bool RestoreLatestModBackup(string folderName);

    /// <summary>The most recent backup's own folder path, or null if this mod has none — for reading a previous version (e.g. comparing what an update changed) without restoring over the current one.</summary>
    string? TryGetLatestModBackupPath(string folderName);

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
