using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.Import;

public sealed class PrebuiltPakImporter(
    IPrebuiltPakToExmodConverter converter, IExmodPackageImporter packageImporter, ILibraryRepository libraryRepository,
    IPrebuiltPakSourceStore sourceStore)
    : IPrebuiltPakImporter
{
    public async Task<LibraryEntry> ImportAsync(
        string pakFilePath, string dataFolder, string? unrealPakExePath,
        string? source = null, int? nexusModId = null, string? catalogEntryId = null,
        string? name = null, string? author = null, CancellationToken cancellationToken = default)
    {
        var pakName = Path.GetFileNameWithoutExtension(pakFilePath);
        var report = new MergeReport();

        // A pak packed for a newer engine is refused up front with the reason (see ModLints) — every
        // pak import path in the app funnels through here, and importing it would only move the
        // failure to an in-game "mod does nothing" with no explanation.
        if (Safety.ModLints.CheckPakVersion(pakFilePath) is { } wrongVersion)
        {
            throw new FormatException(wrongVersion);
        }

        var converted = await converter.TryConvertAsync(
            pakFilePath, dataFolder, unrealPakExePath ?? "", name ?? pakName, author ?? "Unknown", report, cancellationToken: cancellationToken);

        if (converted is null)
        {
            var opaque = libraryRepository.ImportPak(pakFilePath, source, nexusModId, catalogEntryId);
            opaque.ImportWarnings = [.. report.Warnings];
            return opaque;
        }

        var entry = packageImporter.ImportPackage(converted.Contents, source, nexusModId, catalogEntryId);
        // See LibraryEntry.ConvertedFromPrebuiltPak — lets a later Nexus/Database link overwrite
        // this entry's still-placeholder Name/Author, unlike an ordinary EXMOD import. Only marked
        // when the conversion actually produced placeholder metadata (the diffing path) — when the
        // pak's own bundled EXMOD was read directly, Name/Author are the real author's own
        // declared values and must NOT be flagged as safe to overwrite later.
        if (!converted.HasAuthorDeclaredMetadata)
        {
            libraryRepository.MarkConvertedFromPrebuiltPak(entry.FolderName);
            entry.ConvertedFromPrebuiltPak = true;
        }
        // Keeps the original pak around (see IPrebuiltPakSourceStore's own doc comment) so a later
        // game update can re-derive a fresher diff instead of this conversion being frozen forever.
        sourceStore.Save(entry.FolderName, pakFilePath);
        entry.ImportWarnings = [.. report.Warnings];
        return entry;
    }
}
