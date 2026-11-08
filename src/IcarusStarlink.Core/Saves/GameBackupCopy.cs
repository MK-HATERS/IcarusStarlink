using System.Globalization;

namespace IcarusStarlink.Core.Saves;

/// <summary>
/// One of the game's own rolling copies of a save file, kept beside it: "Profile.json.backup"
/// (the newest) and "Profile.json.backup_1" … "_10", the same for each world in Prospects. The
/// game shifts them along at every save, so a listing is only good until the game saves again.
/// </summary>
/// <param name="FilePath">The copy itself, in the same folder as the file it is a copy of.</param>
/// <param name="OriginalFileName">The file it is a copy of ("Profile.json").</param>
/// <param name="Number">0 for ".backup", N for ".backup_N".</param>
/// <param name="WrittenUtc">The copy's last-write time when it was listed (a restore checks it is still the same).</param>
/// <param name="Length">The copy's size when it was listed.</param>
public sealed record GameBackupCopy(string FilePath, string OriginalFileName, int Number, DateTime WrittenUtc, long Length)
{
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>"28/10/2026 14:03 — Profile.json (game copy 3)".</summary>
    public string Display => $"{WrittenUtc.ToLocalTime():g} — {OriginalFileName} ({(Number == 0 ? "game's newest copy" : $"game copy {Number}")})";

    private const string Marker = ".backup";

    /// <summary>
    /// Reads a game copy's name: "X.json.backup" (number 0) or "X.json.backup_N" (number N).
    /// Anything else ("X.json.bak", "X.json.backup_old", ".backup" alone) is not one.
    /// </summary>
    public static bool TryParseName(string fileName, out string originalFileName, out int number)
    {
        originalFileName = "";
        number = 0;
        var at = fileName.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (at <= 0)
        {
            return false;
        }

        var rest = fileName[(at + Marker.Length)..];
        if (rest.Length > 0)
        {
            if (rest[0] != '_' || rest.Length < 2 || rest.Length > 4 || !rest[1..].All(char.IsAsciiDigit)
                || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out number))
            {
                return false;
            }
        }

        originalFileName = fileName[..at];
        return true;
    }
}
