namespace IcarusStarlink.Diffing;

/// <summary>
/// Equality for a (CurrentFile, ItemName, FieldName) key where CurrentFile is compared
/// case-insensitively (it denotes a real Windows file path — different EXMOD authors' tools
/// aren't guaranteed to emit it with consistent casing, and two mods editing "the same" file
/// under different casing must still land in the same merge-conflict group) while ItemName/
/// FieldName stay case-sensitive (real JSON property/row names, not file paths).
/// </summary>
public sealed class FieldChangeKeyComparer : IEqualityComparer<(string CurrentFile, string ItemName, string FieldName)>
{
    public static readonly FieldChangeKeyComparer Instance = new();

    public bool Equals((string CurrentFile, string ItemName, string FieldName) x, (string CurrentFile, string ItemName, string FieldName) y) =>
        string.Equals(x.CurrentFile, y.CurrentFile, StringComparison.OrdinalIgnoreCase)
        && x.ItemName == y.ItemName
        && x.FieldName == y.FieldName;

    public int GetHashCode((string CurrentFile, string ItemName, string FieldName) obj) =>
        HashCode.Combine(obj.CurrentFile.ToUpperInvariant(), obj.ItemName, obj.FieldName);
}
