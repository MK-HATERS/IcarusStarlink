using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// One "File_Items" entry: a DataTable row name plus whichever fields the mod changed on it.
/// Fields is open-ended by design — real samples show only the properties that actually
/// changed, never a full row copy — so it's a plain dictionary rather than a fixed POCO shape.
/// </summary>
public sealed class ExmodFileItem
{
    public required string Name { get; set; }

    public Dictionary<string, JsonNode?> Fields { get; set; } = [];
}
