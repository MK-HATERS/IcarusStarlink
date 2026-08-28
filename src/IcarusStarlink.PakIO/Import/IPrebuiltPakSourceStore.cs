namespace IcarusStarlink.PakIO.Import;

/// <summary>
/// Keeps a permanent copy of the original .pak behind every mod this app has ever converted from
/// an opaque prebuilt pak into a real EXMOD — one file per mod, keyed by its own stable Library
/// folder name. A conversion's field diff only ever reflects the base game data available the
/// moment it ran; keeping the source pak lets a later "Update data folder" run re-derive a fresh
/// diff from the exact same original content, instead of the converted EXMOD being frozen forever
/// at whatever the game looked like the day it was first converted.
/// </summary>
public interface IPrebuiltPakSourceStore
{
    /// <summary>Copies pakFilePath in, keyed by folderName — overwrites any previous source saved under the same name.</summary>
    void Save(string folderName, string pakFilePath);

    /// <summary>The saved source pak's real path, or null if this mod was never converted from a prebuilt pak (or predates this store).</summary>
    string? TryGetPath(string folderName);

    void Delete(string folderName);
}
