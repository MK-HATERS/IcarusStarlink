using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Import patch, as the Server page's "Apply a patch to this server" calls it: Merge &amp; Install
/// does the Library side (its mods, downloads, UE4SS mods and settings, the profile) and the
/// Server page then puts the UE4SS mods on the server. Implemented by MergeInstallViewModel; a
/// seam so the Server page's tests can stand in a fake.
/// </summary>
public interface IPatchImporter
{
    /// <summary>Imports an already-read patch; returns the status line Merge &amp; Install shows for it, or null when it was refused (a build in progress).</summary>
    Task<string?> ImportPatchContentsAsync(PatchImportContents contents);
}
