using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.Core.Saves;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.Storage;

namespace IcarusStarlink.Storage.Saves;

/// <summary>
/// Real-filesystem side of ISaveRepository. Two format facts this encodes, both confirmed against
/// the user's own live save rather than assumed: Characters.json is JSON-in-JSON (a
/// "Characters.json" array of ESCAPED JSON STRINGS, each string itself tab-indented CRLF JSON),
/// and Profile.json is plain JSON. Writes serialize with the game's own style — tab indentation,
/// CRLF — so a diff of an edited file against the game's own output shows only the edited values.
/// </summary>
public sealed class SaveRepository(string playerDataDirectory, string backupsDirectory, ISteamInstallLocator steamLocator) : ISaveRepository
{
    private const string CharactersFileName = "Characters.json";
    private const string ProfileFileName = "Profile.json";
    private const string CharactersArrayKey = "Characters.json";
    private const string AccoladesFileName = "Accolades.json";
    private const string BestiaryFileName = "BestiaryData.json";
    private const string MetaInventoryFileName = "MetaInventory.json";
    private const string MountsFileName = "Mounts.json";

    /// <summary>Matches the game's own formatting (observed: tabs + CRLF), so edited files diff cleanly against game-written ones.</summary>
    private static readonly JsonSerializerOptions GameStyleJson = new()
    {
        WriteIndented = true,
        IndentCharacter = '\t',
        IndentSize = 1,
        NewLine = "\r\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public IReadOnlyList<SaveSlot> ListSlots()
    {
        if (!Directory.Exists(playerDataDirectory))
        {
            return [];
        }

        return [.. Directory.GetDirectories(playerDataDirectory)
            .Select(folder => new { Folder = folder, Name = Path.GetFileName(folder)! })
            // A slot is a SteamID64-named folder that actually contains player data — the filter
            // keeps stray folders (tools, manual backups someone made in place) out of the list.
            .Where(x => x.Name.All(char.IsDigit) && File.Exists(Path.Combine(x.Folder, ProfileFileName)))
            .Select(x => new SaveSlot(x.Name, x.Folder, steamLocator.TryGetPersonaName(x.Name)))
            .OrderBy(s => s.SteamId, StringComparer.Ordinal)];
    }

    public JsonObject LoadProfile(string steamId) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ResolveSlot(steamId), ProfileFileName))) as JsonObject
            ?? throw new FormatException($"{ProfileFileName} isn't a JSON object.");

    public IReadOnlyList<JsonObject> LoadCharacters(string steamId)
    {
        var path = Path.Combine(ResolveSlot(steamId), CharactersFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new FormatException($"{CharactersFileName} isn't a JSON object.");
        if (root[CharactersArrayKey] is not JsonArray array)
        {
            return [];
        }

        var characters = new List<JsonObject>();
        foreach (var element in array)
        {
            // Each element is an escaped JSON STRING, not an object — the format's own quirk,
            // unwrapped here so nothing above this layer ever deals with it.
            if (element?.GetValue<string>() is { } raw && JsonNode.Parse(raw) is JsonObject character)
            {
                characters.Add(character);
            }
        }

        return characters;
    }

    public string? SaveProfile(string steamId, JsonObject profile, bool takeBackup = true) =>
        SaveObject(steamId, ProfileFileName, profile, takeBackup);

    public string? SaveCharacters(string steamId, IReadOnlyList<JsonObject> characters, bool takeBackup = true)
    {
        var backupPath = takeBackup ? BackupSlot(steamId) : null;

        // Re-wrap into the JSON-in-JSON shape: each character serialized as its own tab-indented
        // CRLF document, then embedded as a string element — the exact structure the game writes.
        var array = new JsonArray();
        foreach (var character in characters)
        {
            array.Add(JsonValue.Create(character.ToJsonString(GameStyleJson)));
        }

        var root = new JsonObject { [CharactersArrayKey] = array };
        JsonFileStore.WriteAtomically(Path.Combine(ResolveSlot(steamId), CharactersFileName), root.ToJsonString(GameStyleJson));
        return backupPath;
    }

    public JsonObject LoadAccolades(string steamId) => LoadOptionalObject(steamId, AccoladesFileName, () => new JsonObject { ["CompletedAccolades"] = new JsonArray() });

    public string? SaveAccolades(string steamId, JsonObject accolades, bool takeBackup = true) =>
        SaveObject(steamId, AccoladesFileName, accolades, takeBackup);

    public JsonObject LoadBestiary(string steamId) => LoadOptionalObject(steamId, BestiaryFileName, () => new JsonObject { ["BestiaryTracking"] = new JsonArray(), ["FishTracking"] = new JsonArray() });

    public string? SaveBestiary(string steamId, JsonObject bestiary, bool takeBackup = true) =>
        SaveObject(steamId, BestiaryFileName, bestiary, takeBackup);

    public JsonObject LoadMetaInventory(string steamId) => LoadOptionalObject(steamId, MetaInventoryFileName, () => new JsonObject { ["InventoryID"] = "MetaInventoryID_Main", ["Items"] = new JsonArray() });

    public string? SaveMetaInventory(string steamId, JsonObject metaInventory, bool takeBackup = true) =>
        SaveObject(steamId, MetaInventoryFileName, metaInventory, takeBackup);

    public JsonObject LoadMounts(string steamId) => LoadOptionalObject(steamId, MountsFileName, () => new JsonObject { ["SavedMounts"] = new JsonArray() });

    public string? SaveMounts(string steamId, JsonObject mounts, bool takeBackup = true) =>
        SaveObject(steamId, MountsFileName, mounts, takeBackup);

    /// <summary>Shared by LoadAccolades/LoadBestiary/LoadMetaInventory — unlike Profile.json (guaranteed present for anything ListSlots calls a real slot), these files may not exist yet for a character that hasn't triggered that system, so a missing file degrades to an empty-shaped default rather than throwing.</summary>
    private JsonObject LoadOptionalObject(string steamId, string fileName, Func<JsonObject> defaultValue)
    {
        var path = Path.Combine(ResolveSlot(steamId), fileName);
        if (!File.Exists(path))
        {
            return defaultValue();
        }

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new FormatException($"{fileName} isn't a JSON object.");
    }

    /// <summary>Mirror image of LoadOptionalObject, shared by SaveProfile/SaveAccolades/SaveBestiary/SaveMetaInventory/SaveMounts — every plain-JsonObject save follows the same backup-then-write shape; only SaveCharacters differs (the JSON-in-JSON re-wrap), so it isn't routed through this.</summary>
    private string? SaveObject(string steamId, string fileName, JsonObject value, bool takeBackup)
    {
        var backupPath = takeBackup ? BackupSlot(steamId) : null;
        JsonFileStore.WriteAtomically(Path.Combine(ResolveSlot(steamId), fileName), value.ToJsonString(GameStyleJson));
        return backupPath;
    }

    public IReadOnlyList<int>? LoadBinaryFlags(string steamId)
    {
        var path = Path.Combine(ResolveSlot(steamId), $"flags_{steamId}.dat");
        if (!File.Exists(path))
        {
            return null;
        }

        // int32 strLen, null-terminated ASCII SteamID, int32 count, count * int32 IDs — the format
        // confirmed by parsing the user's real file (strLen counts the null terminator).
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8)
        {
            throw new FormatException($"'{Path.GetFileName(path)}' is too short to be a flags file.");
        }

        var strLen = BitConverter.ToInt32(bytes, 0);
        // long, not int, for the same reason the count-derived offset just below already promotes
        // to 4L: strLen comes straight from a corrupted/malformed file's own first 4 bytes, and for
        // a handful of values near int.MaxValue, plain "4 + strLen" wraps into a negative int32
        // offset that then slips past the bounds check below (bytes.Length is never "less than" a
        // negative number) and reaches BitConverter.ToInt32 with a negative index — throwing
        // ArgumentOutOfRangeException instead of the intended FormatException, which the one real
        // caller (SavesViewModel.BuildBinaryFlags) doesn't catch, failing the WHOLE save slot load
        // instead of just hiding this one binary-flags section as the code's own design intends.
        var countOffset = 4L + strLen;
        if (strLen < 1 || bytes.Length < countOffset + 4)
        {
            throw new FormatException($"'{Path.GetFileName(path)}' has an invalid header.");
        }

        var count = BitConverter.ToInt32(bytes, (int)countOffset);
        var idsOffset = countOffset + 4;
        if (count < 0 || bytes.Length < idsOffset + count * 4L)
        {
            throw new FormatException($"'{Path.GetFileName(path)}' declares {count} flags but is truncated.");
        }

        var ids = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            ids.Add(BitConverter.ToInt32(bytes, (int)idsOffset + i * 4));
        }

        return ids;
    }

    public string? SaveBinaryFlags(string steamId, IReadOnlyList<int> flagIds, bool takeBackup = true)
    {
        var slotFolder = ResolveSlot(steamId);
        var backupPath = takeBackup ? BackupSlot(steamId) : null;

        var idBytes = System.Text.Encoding.ASCII.GetBytes(steamId);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(idBytes.Length + 1); // length includes the null terminator
            writer.Write(idBytes);
            writer.Write((byte)0);
            writer.Write(flagIds.Count);
            foreach (var id in flagIds)
            {
                writer.Write(id);
            }
        }

        JsonFileStore.WriteBytesAtomically(Path.Combine(slotFolder, $"flags_{steamId}.dat"), stream.ToArray());
        return backupPath;
    }

    public string BackupSlot(string steamId)
    {
        var slotFolder = ResolveSlot(steamId);
        Directory.CreateDirectory(backupsDirectory);
        var zipPath = FolderBackup.MakeUniqueTimestampedPath(backupsDirectory, steamId, DateTimeOffset.Now, ".zip");

        // Written under a temp name first, then renamed into place only once ZipFile has finished
        // writing every entry — an interruption partway through (disk full, a save file the still-
        // running game has locked) then leaves nothing under backupsDirectory matching the
        // "{steamId}_*.zip" glob ListBackups/RestoreSlot use, instead of a truncated zip that would
        // otherwise be offered — and later trusted — as a real, complete backup.
        var tempPath = zipPath + ".tmp";
        ZipFile.CreateFromDirectory(slotFolder, tempPath);
        File.Move(tempPath, zipPath);

        PruneBackups(steamId);
        return zipPath;
    }

    /// <summary>
    /// Keeps a slot's backups at MaxBackupsPerSlot — but the single OLDEST zip is always kept
    /// regardless of age: the first backup ever taken is the slot's pristine pre-editor state,
    /// exactly the one a user wants back if a long chain of edits went somewhere bad. So pruning
    /// removes from the middle: the original survives, the newest (cap - 1) survive.
    /// </summary>
    private const int MaxBackupsPerSlot = 10;

    private void PruneBackups(string steamId)
    {
        var backups = Directory.GetFiles(backupsDirectory, $"{steamId}_*.zip")
            .OrderBy(File.GetLastWriteTimeUtc)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (backups.Count <= MaxBackupsPerSlot)
        {
            return;
        }

        // Index 0 is the protected original; everything else is prunable, oldest-first.
        var prunable = backups.Skip(1).ToList();
        foreach (var path in prunable.Take(prunable.Count - (MaxBackupsPerSlot - 1)))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A locked zip (open in Explorer/7-Zip) just stays until next prune — never worth
                // failing the backup that triggered this housekeeping.
            }
        }
    }

    public IReadOnlyList<SaveBackupInfo> ListBackups(string steamId)
    {
        if (!Directory.Exists(backupsDirectory))
        {
            return [];
        }

        return [.. Directory.GetFiles(backupsDirectory, $"{steamId}_*.zip")
            .Select(f => new SaveBackupInfo(f, File.GetLastWriteTimeUtc(f)))
            .OrderByDescending(b => b.TakenAtUtc)];
    }

    public string RestoreSlot(string steamId, string backupFilePath)
    {
        if (!File.Exists(backupFilePath))
        {
            throw new FileNotFoundException($"Backup '{backupFilePath}' doesn't exist.");
        }

        var slotFolder = ResolveSlot(steamId);

        // The spec's own safety rule, verbatim: "Restore writes a pre_restore safety zip first,
        // then replaces the slot" — so even a restore of the wrong backup is itself undoable.
        // Same temp-name-then-rename pattern as BackupSlot's own zip write, for the same reason:
        // this is the one safety net an interrupted restore (the very moment this whole method
        // exists to protect against) needs to still be trustworthy, not a truncated zip nothing
        // ever checks before offering it back as "the state right before this restore."
        Directory.CreateDirectory(backupsDirectory);
        var preRestorePath = FolderBackup.MakeUniqueTimestampedPath(backupsDirectory, $"{steamId}_pre_restore", DateTimeOffset.Now, ".zip");
        var preRestoreTempPath = preRestorePath + ".tmp";
        ZipFile.CreateFromDirectory(slotFolder, preRestoreTempPath);
        File.Move(preRestoreTempPath, preRestorePath);

        // Extracted to a scratch folder FIRST, while the live slot is still fully intact — a
        // corrupt or truncated backup zip (a slow-drive copy, a file that was mid-write when
        // copied) then throws here with the real save folder never having been touched at all,
        // instead of deleting it before discovering the backup can't actually be read. The scratch
        // folder is a sibling of slotFolder (not under backupsDirectory) specifically so the final
        // swap below is a same-volume Directory.Move, not a cross-volume one that could itself fail
        // partway through.
        var scratchFolder = Path.Combine(playerDataDirectory, $"{steamId}_restore_scratch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchFolder);
        try
        {
            ZipFile.ExtractToDirectory(backupFilePath, scratchFolder);
        }
        catch
        {
            // Nothing has touched the live slot yet at this point — safe to just clean up the
            // not-yet-used scratch folder and rethrow.
            if (Directory.Exists(scratchFolder))
            {
                Directory.Delete(scratchFolder, recursive: true);
            }

            throw;
        }

        // Replace, not merge: a restore means "the slot as it was then", and files created since
        // the backup (a new character's sidecar, the game's own rolling .backup copies) lingering
        // beside restored ones would be a half-and-half state neither the user nor the game chose.
        //
        // Deliberately NOT "delete slotFolder, then move scratchFolder into place, then on any
        // failure delete scratchFolder": that shape has a real gap once the delete has already
        // succeeded — if the move that follows it then fails (e.g. a file inside scratchFolder
        // gets briefly locked by a real-time antivirus scan right after extraction, a common
        // Windows occurrence), the live slot is already gone AND the only remaining copy of the
        // restored data gets deleted by the same catch, destroying both the original and the
        // restore. Renaming the live slot out of the way first (a same-volume rename, not a
        // recursive delete, so it can't fail partway through the way a big recursive delete can)
        // means there is never a moment where neither the original nor the replacement exists on
        // disk — and if the final swap-in still fails for some reason, the original is renamed
        // back rather than anything being deleted.
        var oldSlotFolder = Path.Combine(playerDataDirectory, $"{steamId}_restore_old_{Guid.NewGuid():N}");
        SwapFolderInPlace(slotFolder, scratchFolder, oldSlotFolder);

        // Pruning runs LAST, only after backupFilePath has already been fully read — running it
        // right after the pre-restore zip (as this used to) could delete backupFilePath itself
        // before extraction, since PruneBackups doesn't know it's about to be needed: the live
        // slot would already be gone with nothing left to extract from.
        PruneBackups(steamId);

        return preRestorePath;
    }

    /// <summary>
    /// Replaces destinationFolder's content with newContentFolder's, never leaving a moment where
    /// neither the original nor the replacement exists on disk: renames destinationFolder out of
    /// the way (a same-volume rename, not a recursive delete, so it can't fail partway through the
    /// way a big recursive delete can), moves newContentFolder into destinationFolder's place, and
    /// only deletes the renamed-away original once that move has confirmed succeeded. If the swap-in
    /// move itself fails (e.g. a file inside newContentFolder gets briefly locked by a real-time
    /// antivirus scan right after extraction — a real, common Windows occurrence, not hypothetical),
    /// the renamed-away original is moved back into place rather than anything being deleted —
    /// deliberately NOT the simpler "delete destinationFolder, then move newContentFolder into
    /// place" shape, whose own failure-cleanup would otherwise delete the only remaining copy of the
    /// replacement data right after the original was already gone, destroying both. Internal (not
    /// private) so this specific safety property is directly unit-testable without needing to force
    /// a real OS-level file lock, which isn't reliably reproducible in an automated test.
    /// </summary>
    internal static void SwapFolderInPlace(string destinationFolder, string newContentFolder, string oldFolderPath)
    {
        Directory.Move(destinationFolder, oldFolderPath);
        try
        {
            Directory.Move(newContentFolder, destinationFolder);
        }
        catch
        {
            // Put the original back rather than leaving destinationFolder missing — but only if
            // nothing already landed there (Directory.Move on Windows is all-or-nothing for a
            // same-volume rename, so this is a defensive check, not an expected case).
            if (!Directory.Exists(destinationFolder))
            {
                Directory.Move(oldFolderPath, destinationFolder);
            }

            throw;
        }

        // Only reached once the new data is confirmed in place — safe to remove the renamed-away
        // original now. A failure here (a locked file) leaves an inert, harmless leftover folder
        // rather than risking any real data.
        try
        {
            Directory.Delete(oldFolderPath, recursive: true);
        }
        catch (IOException)
        {
            // Same reasoning as PruneBackups' own locked-file tolerance — never worth failing an
            // otherwise-successful restore over housekeeping.
        }
    }

    private string ResolveSlot(string steamId)
    {
        var folder = Path.Combine(playerDataDirectory, steamId);
        return Directory.Exists(folder)
            ? folder
            : throw new DirectoryNotFoundException($"No save slot for '{steamId}' under '{playerDataDirectory}'.");
    }

}
