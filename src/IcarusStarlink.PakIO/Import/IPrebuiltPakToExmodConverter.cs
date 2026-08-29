using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Import;

public interface IPrebuiltPakToExmodConverter
{
    /// <summary>
    /// Attempts to turn a prebuilt/opaque .pak into a real, editable EXMOD by extracting it and
    /// diffing its own DataTable JSON against current base game data — the same field-level
    /// change a mod author would have declared, if this pak had shipped as an EXMOD to begin
    /// with. name/author become the resulting package's own Name/Author (the caller synthesizes
    /// these however it already does for an opaque entry — Nexus metadata, or a generic
    /// fallback); the package's FileName is always derived from the pak's own real filename, not
    /// from name, since the pak's filename is already guaranteed a safe identifier while a
    /// display name isn't.
    ///
    /// Returns null (never throws) on any failure that makes conversion impossible right now — no
    /// UnrealPak.exe configured yet, the extracted game data folder is missing/empty, the pak
    /// itself can't be read — so a caller can always fall back to importing the pak as-is. The
    /// failure is still recorded on report as a warning either way, so it's visible somewhere.
    /// </summary>
    Task<PrebuiltPakConversionResult?> TryConvertAsync(
        string pakFilePath, string dataFolder, string unrealPakExePath, string name, string author,
        MergeReport report, CancellationToken cancellationToken = default);
}

/// <summary>
/// HasAuthorDeclaredMetadata distinguishes the two real conversion strategies for the caller's own
/// sake — it's true only when the pak's own bundled EXMOD was read directly, so Contents.Package's
/// Name/Author/Version/Description are the real author's own declared values, not the generic
/// caller-supplied placeholders a diffed conversion produces. A caller must NOT mark an entry
/// ConvertedFromPrebuiltPak (which tells FolderLibraryRepository.ToEntry it's safe to let a later
/// Nexus/Database link overwrite Name/Author) when this is true — doing so would let Nexus's own
/// title/uploader silently replace a name the author themselves already declared correctly.
/// </summary>
public sealed record PrebuiltPakConversionResult(ExmodPackageContents Contents, bool HasAuthorDeclaredMetadata);
