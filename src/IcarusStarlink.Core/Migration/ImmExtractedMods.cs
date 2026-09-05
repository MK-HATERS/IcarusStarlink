using System.Text.Json;

namespace IcarusStarlink.Core.Migration;

/// <summary>One entry from classic IMM's own ExtractedMods.json library cache. FolderName is the real subfolder under its Extracted_Mods directory — the link a merged-mod-list display name alone doesn't give you.</summary>
public sealed record ImmExtractedMod(string Name, string Author, string Version, string FolderName);

/// <summary>
/// Reads classic IMM's own `ExtractedMods.json` — its flat cache of every mod it has extracted,
/// confirmed against a real install: `{"mods":[{"name","displayname","author","version","fileName"}]}`
/// where fileName is the folder name under Extracted_Mods.
///
/// This is what makes migration possible at all: a merged mod list (LastMergedMods.txt) records
/// only display names, which don't reliably match folder names ("Shengong_Invincible" lives in
/// "BF_ShengongArmour_V1.4"), and it carries no author — the field catalog matching needs.
/// </summary>
public static class ImmExtractedMods
{
    public const string FileName = "ExtractedMods.json";

    public static IReadOnlyList<ImmExtractedMod> Parse(string json)
    {
        var mods = new List<ImmExtractedMod>();

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("mods", out var modsArray) || modsArray.ValueKind != JsonValueKind.Array)
        {
            return mods;
        }

        foreach (var element in modsArray.EnumerateArray())
        {
            // TryGetProperty (inside ReadString below) throws InvalidOperationException if element
            // itself isn't a JSON object — a real risk here since this file is produced/maintained by
            // the classic, legacy IMM tool this app doesn't control, and a single malformed entry
            // (hand-edited, or partially written by a crash in that old tool) would otherwise abort
            // the ENTIRE migration for every other mod in the list, not just this one. Skipped the
            // same way a missing fileName already is below — one bad entry losing its own
            // enrichment data is never a reason to fail every other mod's migration.
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var folderName = ReadString(element, "fileName");
            if (string.IsNullOrWhiteSpace(folderName))
            {
                // Without a folder name there's nothing to copy from — a display name alone can't
                // locate the mod on disk.
                continue;
            }

            mods.Add(new ImmExtractedMod(
                Name: ReadString(element, "name") ?? folderName,
                Author: ReadString(element, "author") ?? "",
                Version: ReadString(element, "version") ?? "",
                FolderName: folderName));
        }

        return mods;
    }

    /// <summary>
    /// Finds the entry a merged-list line refers to. Real lists mix conventions — some lines are
    /// the display name, some are already the folder name — so both are matched, display name
    /// first (that's what the list is nominally written in).
    /// </summary>
    public static ImmExtractedMod? Find(IReadOnlyList<ImmExtractedMod> mods, string listName) =>
        mods.FirstOrDefault(m => string.Equals(m.Name, listName, StringComparison.OrdinalIgnoreCase))
        ?? mods.FirstOrDefault(m => string.Equals(m.FolderName, listName, StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
