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

    /// <summary>Backs up the whole slot first, then writes atomically (temp + move). Returns the backup path so the UI can say where the safety copy went — or null if takeBackup is false (a caller that already took its own backup this same save pass, to avoid zipping the same slot several times over for one user action).</summary>
    string? SaveProfile(string steamId, JsonObject profile, bool takeBackup = true);

    /// <summary>Same contract as SaveProfile. The list replaces the file's character array wholesale, so duplicate/remove-slot operations are just list operations for the caller.</summary>
    string? SaveCharacters(string steamId, IReadOnlyList<JsonObject> characters, bool takeBackup = true);

    /// <summary>Accolades.json as a mutable tree — CompletedAccolades (the editable list) plus PlayerTrackers/PlayerTaskListTrackers (the raw progress counters that make an accolade eligible), which this editor doesn't expose for direct editing but preserves untouched. Returns an empty-shaped object (not an error) if the file doesn't exist yet — a very new character may not have triggered the accolade system.</summary>
    JsonObject LoadAccolades(string steamId);

    /// <summary>Same contract as SaveProfile.</summary>
    string? SaveAccolades(string steamId, JsonObject accolades, bool takeBackup = true);

    /// <summary>BestiaryData.json as a mutable tree — BestiaryTracking (creature encounter points, the editable list) plus FishTracking (preserved untouched, out of scope for this editor). Returns an empty-shaped object if the file doesn't exist yet.</summary>
    JsonObject LoadBestiary(string steamId);

    /// <summary>Same contract as SaveProfile.</summary>
    string? SaveBestiary(string steamId, JsonObject bestiary, bool takeBackup = true);

    /// <summary>MetaInventory.json as a mutable tree — the account-wide meta item bank (InventoryID + a flat Items array). Returns an empty-shaped object if the file doesn't exist yet.</summary>
    JsonObject LoadMetaInventory(string steamId);

    /// <summary>Same contract as SaveProfile.</summary>
    string? SaveMetaInventory(string steamId, JsonObject metaInventory, bool takeBackup = true);

    /// <summary>
    /// The slot's binary flags_&lt;SteamID&gt;.dat — a THIRD unlock store the game keeps beside the
    /// JSON ones (confirmed against a real save: account-wide character-flag IDs like
    /// Talent_RepairBench and Mission_Olympus_Unlock live here, NOT in any UnlockedFlags array).
    /// Format, confirmed by parsing the real file: int32 string length, null-terminated ASCII
    /// SteamID, int32 count, then count int32 flag IDs. Null when the slot has no such file —
    /// the editor shows the section only when the game itself has created one.
    /// </summary>
    IReadOnlyList<int>? LoadBinaryFlags(string steamId);

    /// <summary>Same contract as SaveProfile: full slot backup first, then an atomic write of the binary flags file. Returns the backup path.</summary>
    string? SaveBinaryFlags(string steamId, IReadOnlyList<int> flagIds, bool takeBackup = true);

    /// <summary>Zips the entire slot folder into this app's own save_backups cache (never inside the game's folders). Returns the zip path.</summary>
    string BackupSlot(string steamId);

    IReadOnlyList<SaveBackupInfo> ListBackups(string steamId);

    /// <summary>Writes a pre_restore safety zip of the slot as it is RIGHT NOW, then replaces the slot's contents with the chosen backup. Returns the pre_restore zip path.</summary>
    string RestoreSlot(string steamId, string backupFilePath);
}
