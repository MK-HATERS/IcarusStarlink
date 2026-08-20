using System.Text;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Library;

/// <summary>
/// The Extracted_Mods folder is the source of truth for each mod's own content; library-only
/// metadata (pin/favorite/notes) lives in a separate metadata directory, keyed by folder name —
/// see LibraryMetaStore for why it can't live inside the mod's own folder. The in-memory entry
/// cache and FTS5 index are both derived from these, never the other way around. A full folder
/// scan only happens once, at construction — Import/Delete/UpdateMetadata each update just the
/// one entry they touched afterward.
/// </summary>
public sealed class FolderLibraryRepository : ILibraryRepository, IDisposable
{
    private readonly string _extractedModsDirectory;
    private readonly LibraryMetaStore _metaStore;
    private readonly LibrarySearchIndex _searchIndex;
    private readonly ILogger<FolderLibraryRepository> _logger;
    private List<LibraryEntry> _cachedEntries = [];

    public FolderLibraryRepository(string extractedModsDirectory, string metaDirectory, ILogger<FolderLibraryRepository> logger)
    {
        _extractedModsDirectory = extractedModsDirectory;
        _logger = logger;
        _metaStore = new LibraryMetaStore(metaDirectory, logger);
        _searchIndex = new LibrarySearchIndex();

        Directory.CreateDirectory(_extractedModsDirectory);
        RescanAll();
    }

    public IReadOnlyList<LibraryEntry> GetAll() => _cachedEntries;

    public IReadOnlyList<LibraryEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _cachedEntries;
        }

        var matchingFolders = _searchIndex.Search(query);
        return [.. _cachedEntries.Where(e => matchingFolders.Contains(e.FolderName))];
    }

    public LibraryEntry Import(string sourcePath)
    {
        if (sourcePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Importing prebuilt .pak files isn't supported yet.");
        }

        var contents = Directory.Exists(sourcePath)
            ? ExmodFolder.Read(sourcePath)
            : ExmodzArchive.Read(sourcePath);

        // Package.FileName is already guaranteed a safe, simple identifier (AssetPathGuard runs
        // inside ExmodJson.Parse) — reuse it as the folder name directly rather than re-deriving
        // one, only disambiguating on collision.
        var folderName = MakeUniqueFolderName(contents.Package.FileName);
        var targetFolder = Path.Combine(_extractedModsDirectory, folderName);

        ExmodFolder.Write(targetFolder, contents);
        var meta = new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow };
        _metaStore.Save(folderName, meta);

        // Surgical: this is the only mod whose content is actually new — no need to re-read
        // every other mod's package just to add one entry.
        var entry = ToEntry(folderName, contents.Package, meta);
        _cachedEntries.Add(entry);
        _searchIndex.Insert(entry, BuildSearchableContent(contents.Package));

        return entry;
    }

    public void Delete(string folderName)
    {
        Directory.Delete(ResolveFolder(folderName), recursive: true);
        // Otherwise a later mod imported with the same folder name would silently inherit this
        // deleted mod's pin/favorite/notes.
        _metaStore.Delete(folderName);

        _cachedEntries.RemoveAll(e => e.FolderName == folderName);
        _searchIndex.Remove(folderName);
    }

    public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes)
    {
        ResolveFolder(folderName); // validates the entry actually exists before writing metadata for it

        var meta = _metaStore.Load(folderName);
        meta.IsPinned = isPinned;
        meta.IsFavorite = isFavorite;
        meta.Notes = notes;
        _metaStore.Save(folderName, meta);

        // Only this entry's sidecar metadata changed — its EXMOD content (and every other mod's)
        // is untouched, so update the one cached entry and its one index row in place rather
        // than re-scanning the whole library.
        var existing = _cachedEntries.Find(e => e.FolderName == folderName);
        if (existing is not null)
        {
            existing.IsPinned = isPinned;
            existing.IsFavorite = isFavorite;
            existing.Notes = notes;
            _searchIndex.UpdateNotes(folderName, notes);
        }
    }

    public IReadOnlyList<string> ListAssetPaths(string folderName) =>
        ExmodFolder.ListAssetPaths(ResolveFolder(folderName));

    public byte[] ReadAssetContent(string folderName, string relativePath) =>
        ExmodFolder.ReadAssetContent(ResolveFolder(folderName), relativePath);

    public string? ReadReadme(string folderName)
    {
        var folder = ResolveFolder(folderName);
        var readmePath = ExmodFolder.ListAssetPaths(folder)
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals("readme", StringComparison.OrdinalIgnoreCase));

        return readmePath is null ? null : Encoding.UTF8.GetString(ExmodFolder.ReadAssetContent(folder, readmePath));
    }

    private void RescanAll()
    {
        var scanned = new List<(LibraryEntry Entry, string SearchableContent)>();

        foreach (var folder in Directory.EnumerateDirectories(_extractedModsDirectory))
        {
            try
            {
                var package = ExmodFolder.ReadPackageOnly(folder);
                var folderName = Path.GetFileName(folder);
                var meta = _metaStore.Load(folderName);
                scanned.Add((ToEntry(folderName, package, meta), BuildSearchableContent(package)));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                // This runs during construction, which the app's default nav page resolves
                // before the main window is even shown — one locked/permission-denied/
                // mid-write folder must not be able to crash the whole app at launch.
                _logger.LogWarning(ex, "Skipped '{Folder}' while scanning the library — could not read it", folder);
            }
        }

        _cachedEntries = [.. scanned.Select(t => t.Entry)];
        _searchIndex.Rebuild(scanned);
    }

    private static LibraryEntry ToEntry(string folderName, ExmodPackage package, LibraryMeta meta) => new()
    {
        FolderName = folderName,
        Name = package.Name,
        Author = package.Author,
        Version = package.Version,
        Description = package.Description,
        FileName = package.FileName,
        VariantGroup = package.VariantGroup,
        Variant = package.Variant,
        VariantSort = package.VariantSort,
        IsPinned = meta.IsPinned,
        IsFavorite = meta.IsFavorite,
        Notes = meta.Notes,
        ImportedAtUtc = meta.ImportedAtUtc,
    };

    private static string BuildSearchableContent(ExmodPackage package)
    {
        var text = new StringBuilder();
        foreach (var row in package.Rows)
        {
            text.Append(row.CurrentFile).Append(' ');
            foreach (var item in row.FileItems)
            {
                text.Append(item.Name).Append(' ');
                foreach (var fieldName in item.Fields.Keys)
                {
                    text.Append(fieldName).Append(' ');
                }
            }
        }

        return text.ToString();
    }

    private string MakeUniqueFolderName(string fileName)
    {
        var candidate = fileName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(_extractedModsDirectory, candidate)))
        {
            candidate = $"{fileName}_{++suffix}";
        }

        return candidate;
    }

    private string ResolveFolder(string folderName)
    {
        var folder = Path.Combine(_extractedModsDirectory, folderName);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"No library entry named '{folderName}'.");
        }

        return folder;
    }

    public void Dispose() => _searchIndex.Dispose();
}
