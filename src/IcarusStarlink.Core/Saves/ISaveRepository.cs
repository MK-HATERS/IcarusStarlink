using System.Text.Json.Nodes;

namespace IcarusStarlink.Core.Saves;

/// <summary>
/// The save editor's own data layer, built around two safety rules the spec itself leads with:
/// every Save* method takes a full slot backup BEFORE writing anything, and Restore writes a
/// pre_restore safety zip before replacing the slot — so there is no code path that changes
/// player data without first capturing what was there.
///
/// Documents are exposed as raw JsonObject trees, NOT typed models: Icarus's save files carry far
/// more fields than this editor understands (cosmetic hashes, flags, timestamps, whole subsystems),
/// and a typed round-trip would silently drop every one of them. Editing the tree in place means
/// unknown fields survive byte-for-value; ViewModels read/write the specific keys they know.
/// </summary>
public interface ISaveRepository
{
    /// <summary>Every slot folder under the game's PlayerData directory. Empty (not an error) when the game has never run on this machine.</summary>
    IReadOnlyList<SaveSlot> ListSlots();

    /// <summary>Profile.json as a mutable tree — currencies (MetaResources), workshop Talents, UnlockedFlags, and whatever else the game keeps there.</summary>
    JsonObject LoadProfile(string steamId);

    /// <summary>
    /// The characters, each as a mutable tree. Characters.json is JSON-in-JSON — a
    /// "Characters.json" array whose elements are ESCAPED JSON STRINGS (confirmed against the real
    /// file, literal \r\n\t formatting inside) — and this unwraps that quirk so callers never see
    /// it; SaveCharacters re-wraps identically.
    /// </summary>
    IReadOnlyList<JsonObject> LoadCharacters(string steamId);

    /// <summary>Backs up the whole slot first, then writes atomically (temp + move). Returns the backup path so the UI can say where the safety copy went.</summary>
    string SaveProfile(string steamId, JsonObject profile);

    /// <summary>Same contract as SaveProfile. The list replaces the file's character array wholesale, so duplicate/remove-slot operations are just list operations for the caller.</summary>
    string SaveCharacters(string steamId, IReadOnlyList<JsonObject> characters);

    /// <summary>Zips the entire slot folder into this app's own save_backups cache (never inside the game's folders). Returns the zip path.</summary>
    string BackupSlot(string steamId);

    IReadOnlyList<SaveBackupInfo> ListBackups(string steamId);

    /// <summary>Writes a pre_restore safety zip of the slot as it is RIGHT NOW, then replaces the slot's contents with the chosen backup. Returns the pre_restore zip path.</summary>
    string RestoreSlot(string steamId, string backupFilePath);
}
