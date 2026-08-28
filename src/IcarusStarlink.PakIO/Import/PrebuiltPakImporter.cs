using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.Import;

public sealed class PrebuiltPakImporter(
    IPrebuiltPakToExmodConverter converter, IExmodPackageImporter packageImporter, ILibraryRepository libraryRepository)
    : IPrebuiltPakImporter
{
    public async Task<LibraryEntry> ImportAsync(
        string pakFilePath, string dataFolder, string? unrealPakExePath,
        string? source = null, int? nexusModId = null, string? catalogEntryId = null,
        string? name = null, string? author = null, CancellationToken cancellationToken = default)
    {
        var pakName = Path.GetFileNameWithoutExtension(pakFilePath);
        var report = new MergeReport();

        var converted = await converter.TryConvertAsync(
            pakFilePath, dataFolder, unrealPakExePath ?? "", name ?? pakName, author ?? "Unknown", report, cancellationToken);

        if (converted is null)
        {
            return libraryRepository.ImportPak(pakFilePath, source, nexusModId, catalogEntryId);
        }

        var entry = packageImporter.ImportPackage(converted, source, nexusModId, catalogEntryId);
        // See LibraryEntry.ConvertedFromPrebuiltPak — lets a later Nexus/Database link overwrite
        // this entry's still-placeholder Name/Author, unlike an ordinary EXMOD import.
        libraryRepository.MarkConvertedFromPrebuiltPak(entry.FolderName);
        entry.ConvertedFromPrebuiltPak = true;
        return entry;
    }
}
