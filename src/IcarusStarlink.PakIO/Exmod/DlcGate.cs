using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// A paid DLC's gate on a game row: Metadata.RequiredFeatureLevel names a D_FeatureLevels row
/// (GreatHunts, DangerousHorizons…). The game's rows without a gate leave Metadata out (or empty),
/// so a row with Metadata but no RequiredFeatureLevel needs no DLC.
/// </summary>
public static partial class DlcGate
{
    public const string MetadataField = "Metadata";
    private const string GateKey = "RequiredFeatureLevel";

    /// <summary>Feature levels every player has: the base game and the free updates (Galileo is fishing, Laika is taming).</summary>
    private static readonly HashSet<string> NotDlc = new(StringComparer.OrdinalIgnoreCase)
    {
        "None", "Core", "FirstCohort", "Styx", "Galileo", "Hypatia", "Laika",
    };

    /// <summary>Whether a feature level is a paid DLC (not the base game or a free update).</summary>
    public static bool IsDlc(string? level) => level is { Length: > 0 } && !NotDlc.Contains(level);

    /// <summary>The paid DLC a row needs (its RequiredFeatureLevel row name); null when it needs none, or only a free update.</summary>
    public static string? Of(JsonObject? row) => OfMetadata(row?[MetadataField]);

    /// <summary>The paid DLC a Metadata value names; null for none.</summary>
    public static string? OfMetadata(JsonNode? metadata) =>
        (metadata as JsonObject)?[GateKey] is JsonObject link && link["RowName"] is JsonValue v && v.TryGetValue<string>(out var level) && IsDlc(level) ? level : null;

    /// <summary>A copy of a Metadata value without the gate (other keys kept); an empty object when it had nothing else.</summary>
    public static JsonObject Without(JsonNode? metadata)
    {
        var copy = metadata is JsonObject o ? (JsonObject)o.DeepClone() : [];
        copy.Remove(GateKey);
        return copy;
    }

    /// <summary>The DLC's name players know (D_FeatureLevels DisplayName, INVTEXT("Great Hunts")); the row name when the table hasn't it.</summary>
    public static string Name(GameTableIndex game, string level)
    {
        var file = game.CurrentFileFor("D_FeatureLevels");
        if ((file is null ? null : game.Row(file, level)?["DisplayName"]) is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return level;
        }

        var shown = NsLocText.TextOf(text) ?? (InvText().Match(text) is { Success: true } m ? m.Groups[1].Value : text);
        return string.IsNullOrWhiteSpace(shown) ? level : shown;
    }

    [GeneratedRegex("""^INVTEXT\("((?:[^"\\]|\\.)*)"\)$""")]
    private static partial Regex InvText();
}
