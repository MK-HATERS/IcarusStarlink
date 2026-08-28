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

    public string? SaveProfile(string steamId, JsonObject profile, bool takeBackup = true)
    {
        var backupPath = takeBackup ? BackupSlot(steamId) : null;
        JsonFileStore.WriteAtomically(
            Path.Combine(ResolveSlot(steamId), ProfileFileName),
            profile.ToJsonString(GameStyleJson));
        return backupPath;
    }

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

    public string? SaveAccolades(string steamId, JsonObject accolades, bool takeBackup = true)
    {
        var backupPath = takeBackup ? BackupSlot(steamId) : null;
        JsonFileStore.WriteAtomically(Path.Combine(ResolveSlot(steamId), AccoladesFileName), accolades.ToJsonString(GameStyleJson));
        return backupPath;
    }

    public JsonObject LoadBestiary(string steamId) => LoadOptionalObject(steamId, BestiaryFileName, () => new JsonObject { ["BestiaryTracking"] = new JsonArray(), ["FishTracking"] = new JsonArray() });

    public string? SaveBestiary(string steamId, JsonObject bestiary, bool takeBackup = true)
    {
        var backupPath = takeBackup ? BackupSlot(steamId) : null;
        JsonFileStore.WriteAtomically(Path.Combine(ResolveSlot(steamId), BestiaryFileName), bestiary.ToJsonString(GameStyleJson));
        return backupPath;
    }

    public JsonObject LoadMetaInventory(string steamId) => LoadOptionalObject(steamId, MetaInventoryFileName, () => new JsonObject { ["InventoryID"] = "MetaInventoryID_Main", ["Items"] = new JsonArray() });

    public string? SaveMetaInventory(string steamId, JsonObject metaInventory, bool takeBackup = true)
    {
        var backupPath = takeBackup ? BackupSlot(steamId) : null;
        JsonFileStore.WriteAtomically(Path.Combine(ResolveSlot(steamId), MetaInventoryFileName), metaInventory.ToJsonString(GameStyleJson));
        return backupPath;
    }

    public JsonObject LoadMounts(string steamId) => LoadOptionalObject(steamId, MountsFileName, () => new JsonObject { ["SavedMounts"] = new JsonArray() });

    public string? SaveMounts(string steamId, JsonObject mounts, bool takeBackup = true)
    {
        var backupPath = takeBackup ? BackupSlot(steamId) : null;
        JsonFileStore.WriteAtomically(Path.Combine(ResolveSlot(steamId), MountsFileName), mounts.ToJsonString(GameStyleJson));
        return backupPath;
    }

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
        var countOffset = 4 + strLen;
        if (strLen < 1 || bytes.Length < countOffset + 4)
        {
            throw new FormatException($"'{Path.GetFileName(path)}' has an invalid header.");
        }

        var count = BitConverter.ToInt32(bytes, countOffset);
        var idsOffset = countOffset + 4;
        if (count < 0 || bytes.Length < idsOffset + count * 4L)
        {
            throw new FormatException($"'{Path.GetFileName(path)}' declares {count} flags but is truncated.");
        }

        var ids = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            ids.Add(BitConverter.ToInt32(bytes, idsOffset + i * 4));
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
        Directory.CreateDirectory(backupsDirectory);
        var preRestorePath = FolderBackup.MakeUniqueTimestampedPath(backupsDirectory, $"{steamId}_pre_restore", DateTimeOffset.Now, ".zip");
        ZipFile.CreateFromDirectory(slotFolder, preRestorePath);

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

            // Replace, not merge: a restore means "the slot as it was then", and files created
            // since the backup (a new character's sidecar, the game's own rolling .backup copies)
            // lingering beside restored ones would be a half-and-half state neither the user nor
            // the game chose. Only reached once the extraction above has already fully succeeded.
            Directory.Delete(slotFolder, recursive: true);
            Directory.Move(scratchFolder, slotFolder);
        }
        catch
        {
            if (Directory.Exists(scratchFolder))
            {
                Directory.Delete(scratchFolder, recursive: true);
            }

            throw;
        }

        // Pruning runs LAST, only after backupFilePath has already been fully read — running it
        // right after the pre-restore zip (as this used to) could delete backupFilePath itself
        // before extraction, since PruneBackups doesn't know it's about to be needed: the live
        // slot would already be gone with nothing left to extract from.
        PruneBackups(steamId);

        return preRestorePath;
    }

    private string ResolveSlot(string steamId)
    {
        var folder = Path.Combine(playerDataDirectory, steamId);
        return Directory.Exists(folder)
            ? folder
            : throw new DirectoryNotFoundException($"No save slot for '{steamId}' under '{playerDataDirectory}'.");
    }

}
