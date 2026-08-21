using System.IO.Compression;
using System.Text;
using System.Text.Json;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Patches;

/// <summary>
/// A patch's zip layout mirrors .EXMODZ's own precedent (ExmodzArchive): one manifest.json entry
/// plus, for each bundled mod, a real standalone .EXMODZ file stored verbatim under
/// Bundled/&lt;FolderName&gt;.EXMODZ — not decomposed, so it can be pulled out and imported on its
/// own with any tool that already reads .EXMODZ, this app included.
/// </summary>
public sealed class PatchService : IPatchService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Task ExportAsync(
        PatchManifest manifest, IReadOnlyDictionary<string, ExmodPackageContents> bundledMods, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        if (bundledMods.Count == 0)
        {
            File.WriteAllText(outputPath, json);
            return Task.CompletedTask;
        }

        using var fileStream = File.Create(outputPath);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var entryStream = manifestEntry.Open())
        using (var writer = new StreamWriter(entryStream))
        {
            writer.Write(json);
        }

        foreach (var (folderName, contents) in bundledMods)
        {
            var entry = archive.CreateEntry($"Bundled/{folderName}.EXMODZ", CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            // NoCompression: the entry already holds a fully-compressed .EXMODZ (ExmodzArchive.Write
            // itself deflates its own contents) — compressing already-compressed bytes again wastes
            // CPU for no size benefit.
            ExmodzArchive.Write(entryStream, contents);
        }

        return Task.CompletedTask;
    }

    public Task<PatchImportContents> ImportAsync(string patchFilePath, CancellationToken cancellationToken = default)
    {
        using var fileStream = File.OpenRead(patchFilePath);

        var header = new byte[4];
        var headerBytesRead = fileStream.Read(header, 0, header.Length);
        fileStream.Position = 0;
        var isZip = headerBytesRead >= 2 && header[0] == 0x50 && header[1] == 0x4B; // 'P' 'K'

        var result = isZip ? ReadZipPatch(fileStream) : ReadJsonPatch(fileStream);
        return Task.FromResult(result);
    }

    private static PatchImportContents ReadJsonPatch(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var manifest = Deserialize(json);
        return new PatchImportContents(manifest, new Dictionary<string, ExmodPackageContents>());
    }

    private static PatchImportContents ReadZipPatch(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var sizeBudget = new ExmodSizeBudget("Patch archive");

        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new FormatException("Patch archive is missing manifest.json.");
        var manifest = Deserialize(Encoding.UTF8.GetString(ReadEntryBytes(manifestEntry, sizeBudget, "manifest.json")));

        var bundledMods = new Dictionary<string, ExmodPackageContents>();
        foreach (var mod in manifest.Mods.Where(m => m.Bundled))
        {
            var entryName = $"Bundled/{mod.FolderName}.EXMODZ";
            var entry = archive.GetEntry(entryName)
                ?? throw new FormatException($"Patch archive is missing the bundled mod '{mod.Name}' ({entryName}).");
            var bytes = ReadEntryBytes(entry, sizeBudget, entryName);
            using var entryContentStream = new MemoryStream(bytes, writable: false);
            bundledMods[mod.FolderName] = ExmodzArchive.Read(entryContentStream);
        }

        return new PatchImportContents(manifest, bundledMods);
    }

    /// <summary>
    /// Reads a zip entry's full content by its own declared (central-directory) length, charged
    /// against sizeBudget first — same declared-size-before-reading + ReadExactly + no-trailing-
    /// data shape ExmodzArchive.Read already uses for its own entries, applied one layer up since
    /// a patch archive is exactly as untrusted (shared by a friend, downloaded from a server).
    /// </summary>
    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, ExmodSizeBudget sizeBudget, string description)
    {
        sizeBudget.Charge(description, entry.Length);

        using var entryStream = entry.Open();
        var buffer = new byte[entry.Length];
        try
        {
            entryStream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new FormatException(
                $"Patch archive entry '{description}' is corrupt — declared {entry.Length:N0} bytes but contained fewer.", ex);
        }

        if (entryStream.ReadByte() != -1)
        {
            throw new FormatException(
                $"Patch archive entry '{description}' is corrupt — contains more data than its declared {entry.Length:N0} byte size.");
        }

        return buffer;
    }

    private static PatchManifest Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PatchManifest>(json, JsonOptions)
                ?? throw new FormatException("Patch manifest is empty or corrupt.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Patch manifest is corrupt.", ex);
        }
    }
}
