using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.Core.Saves;
using IcarusStarlink.Core.Steam;

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

    public string SaveProfile(string steamId, JsonObject profile)
    {
        var backupPath = BackupSlot(steamId);
        WriteAtomically(
            Path.Combine(ResolveSlot(steamId), ProfileFileName),
            profile.ToJsonString(GameStyleJson));
        return backupPath;
    }

    public string SaveCharacters(string steamId, IReadOnlyList<JsonObject> characters)
    {
        var backupPath = BackupSlot(steamId);

        // Re-wrap into the JSON-in-JSON shape: each character serialized as its own tab-indented
        // CRLF document, then embedded as a string element — the exact structure the game writes.
        var array = new JsonArray();
        foreach (var character in characters)
        {
            array.Add(JsonValue.Create(character.ToJsonString(GameStyleJson)));
        }

        var root = new JsonObject { [CharactersArrayKey] = array };
        WriteAtomically(Path.Combine(ResolveSlot(steamId), CharactersFileName), root.ToJsonString(GameStyleJson));
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

    public string SaveBinaryFlags(string steamId, IReadOnlyList<int> flagIds)
    {
        var slotFolder = ResolveSlot(steamId);
        var backupPath = BackupSlot(steamId);

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

        WriteBytesAtomically(Path.Combine(slotFolder, $"flags_{steamId}.dat"), stream.ToArray());
        return backupPath;
    }

    public string BackupSlot(string steamId)
    {
        var slotFolder = ResolveSlot(steamId);
        Directory.CreateDirectory(backupsDirectory);
        var zipPath = Path.Combine(backupsDirectory, $"{steamId}_{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");

        // A same-second second backup (rapid Save clicks) must not collide or overwrite.
        var suffix = 1;
        while (File.Exists(zipPath))
        {
            zipPath = Path.Combine(backupsDirectory, $"{steamId}_{DateTimeOffset.Now:yyyyMMdd-HHmmss}_{++suffix}.zip");
        }

        ZipFile.CreateFromDirectory(slotFolder, zipPath);
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
        var preRestorePath = Path.Combine(backupsDirectory, $"{steamId}_pre_restore_{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        var suffix = 1;
        while (File.Exists(preRestorePath))
        {
            preRestorePath = Path.Combine(backupsDirectory, $"{steamId}_pre_restore_{DateTimeOffset.Now:yyyyMMdd-HHmmss}_{++suffix}.zip");
        }

        ZipFile.CreateFromDirectory(slotFolder, preRestorePath);
        PruneBackups(steamId);

        // Replace, not merge: a restore means "the slot as it was then", and files created since
        // the backup (a new character's sidecar, the game's own rolling .backup copies) lingering
        // beside restored ones would be a half-and-half state neither the user nor the game chose.
        Directory.Delete(slotFolder, recursive: true);
        Directory.CreateDirectory(slotFolder);
        ZipFile.ExtractToDirectory(backupFilePath, slotFolder);

        return preRestorePath;
    }

    private string ResolveSlot(string steamId)
    {
        var folder = Path.Combine(playerDataDirectory, steamId);
        return Directory.Exists(folder)
            ? folder
            : throw new DirectoryNotFoundException($"No save slot for '{steamId}' under '{playerDataDirectory}'.");
    }

    /// <summary>Temp-then-move, same crash-safety reasoning as JsonFileStore.Save — a kill mid-write must never leave a player's save file truncated.</summary>
    private static void WriteAtomically(string filePath, string content)
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);
    }

    private static void WriteBytesAtomically(string filePath, byte[] bytes)
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, filePath, overwrite: true);
    }
}
