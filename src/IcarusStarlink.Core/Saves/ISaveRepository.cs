using System.Text.Json.Nodes;

namespace IcarusStarlink.Core.Saves;

/// <summary>
/// The save editor's own data layer, built around two safety rules:
/// every Save* method takes a backup of the slot's player files BEFORE writing anything, and
/// Restore writes a pre_restore safety zip before replacing them — so there is no code path that
/// changes player data without first capturing what was there.
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

    /// <summary>Backs up the slot's player files first, then writes atomically (temp + move). Returns the backup path so the UI can say where the safety copy went — or null if takeBackup is false (a caller that already took its own backup this same save pass, to avoid zipping the same slot several times over for one user action).</summary>
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
    /// Mounts.json as a mutable tree — SavedMounts, a flat array of tamed mounts. Each mount's
    /// MountName/MountLevel/MountType are plain, safely editable fields; its real stats/stomach/
    /// saddle state lives in RecorderBlob, a raw Unreal binary-serialized blob (confirmed against a
    /// real save — not JSON). The editor changes it only through MountBlob, and only for a mount
    /// whose blob reads and writes back byte for byte. Returns an empty-shaped object if the file
    /// doesn't exist yet.
    /// </summary>
    JsonObject LoadMounts(string steamId);

    /// <summary>Same contract as SaveProfile.</summary>
    string? SaveMounts(string steamId, JsonObject mounts, bool takeBackup = true);

    /// <summary>
    /// The slot's binary flags_&lt;SteamID&gt;.dat: the game's cached list of the DLC this account
    /// owns, as 0-based row numbers of D_DLCPackageData. The game rebuilds it from Steam when it
    /// starts. Format, confirmed by parsing the real file: int32 string length, null-terminated
    /// ASCII SteamID, int32 count, then count int32 IDs. Null when the slot has no such file —
    /// the editor shows the section only when the game itself has created one.
    /// </summary>
    IReadOnlyList<int>? LoadBinaryFlags(string steamId);

    /// <summary>Same contract as SaveProfile: a backup of the slot's player files first, then an atomic write of the binary flags file. Returns the backup path.</summary>
    string? SaveBinaryFlags(string steamId, IReadOnlyList<int> flagIds, bool takeBackup = true);

    /// <summary>Zips the slot's top-level player files into this app's own save_backups cache (never inside the game's folders). Prospects (worlds), map data and loadouts are never in it. Returns the zip path.</summary>
    string BackupSlot(string steamId);

    /// <summary>The user's own deliberate "Backup now": named distinctly ("…_manual_…") and never pruned by the automatic Save/Restore backup cap, so the one backup they took on purpose can't silently vanish after nine more clicks.</summary>
    string BackupSlotManually(string steamId);

    /// <summary>
    /// The backup Find the broken mod takes before it changes the install: the same player files
    /// as BackupSlot, named "…_before_find_broken_mod_…". Only the newest 3 of this kind are kept
    /// per slot; the automatic Save/Restore cap and the "Backup now" zips leave them alone, and
    /// pruning them never touches another kind. Returns the zip path.
    /// </summary>
    string BackupSlotBeforeFindBrokenMod(string steamId);

    IReadOnlyList<SaveBackupInfo> ListBackups(string steamId);

    /// <summary>Settings › Maintenance › Backups to keep › Save backups (each slot): how many automatic backups are kept, also each world's on the Worlds tab.</summary>
    int AutomaticBackupsKept => Settings.BackupLimits.DefaultSaveBackupsKept;

    /// <summary>Writes a pre_restore safety zip of the slot as it is RIGHT NOW, then replaces the slot's contents with the chosen backup. Returns the pre_restore zip path.</summary>
    string RestoreSlot(string steamId, string backupFilePath);

    /// <summary>
    /// The game's own rolling copies of the slot's player files ("Profile.json.backup",
    /// "Characters.json.backup_3"…), newest first, read fresh each call (the game shifts them at
    /// every save). Empty when there are none.
    /// </summary>
    IReadOnlyList<GameBackupCopy> ListGameBackups(string steamId) => [];

    /// <summary>
    /// Puts one of the game's own copies back under its file's real name: the copy is read and
    /// checked first, then a pre_restore zip of the slot is written, then the file is written
    /// atomically and its time set to now. The copy itself is only read, never renamed or moved.
    /// Throws, changing nothing, when the copy changed since it was listed. Returns the pre_restore zip path.
    /// </summary>
    string RestoreGameBackup(string steamId, GameBackupCopy copy) => throw new NotSupportedException("This save store can't restore the game's own copies.");

    /// <summary>
    /// For a Save that failed partway: puts the named top-level files (the ones it had already
    /// written) back as they are in the backup it took just before. A file the backup has is
    /// written back atomically; one it doesn't have (the failed Save made it) is deleted. No
    /// pre_restore zip is taken, so the newest backup is still the one from just before that Save.
    /// Throws when a file can't be put back (the game or Steam holds it).
    /// </summary>
    void PutBackFiles(string steamId, string backupFilePath, IReadOnlyCollection<string> fileNames);

    /// <summary>
    /// Last-write time and size of every file the editor reads (Profile, Characters, Accolades,
    /// Bestiary, MetaInventory, Mounts, flags_&lt;id&gt;.dat), keyed by file name; null for a file
    /// that doesn't exist. The Saves page records these at load and compares before writing, so a
    /// save the game rewrote in the meantime (the user played with the editor open) is never
    /// overwritten by the older copy it read.
    /// </summary>
    IReadOnlyDictionary<string, SaveFileStamp?> GetFileStamps(string steamId);
}
