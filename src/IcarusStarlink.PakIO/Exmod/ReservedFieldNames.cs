namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// EXMOD's flat JSON shape has no way to represent a field literally named "Name" — it collides
/// with (and, if written, silently overwrites) the row-identity key, then is lost entirely on
/// the next parse (Parse skips "Name" when populating Fields). Enforced at two layers that see
/// this collision from different angles — ExmodFieldChangeMapper.FromFieldChanges (fails fast,
/// with the offending FieldChange's own context) and ExmodJson.ToJsonObject (defense in depth
/// for any ExmodFileItem built some other way) — kept as one shared throw so the message only
/// has to change in one place.
/// </summary>
internal static class ReservedFieldNames
{
    public static void EnsureFieldNameAllowed(string itemName, string currentFile, string fieldName)
    {
        if (fieldName == "Name")
        {
            throw new FormatException(
                $"Item '{itemName}' in file '{currentFile}' has a field literally named 'Name', " +
                "which collides with the row-identity key and cannot be represented in EXMOD JSON.");
        }
    }
}
