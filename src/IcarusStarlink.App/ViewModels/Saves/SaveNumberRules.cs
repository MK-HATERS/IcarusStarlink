using System.Text.RegularExpressions;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// What a Saves page number box may hold, in one place: Save's refusal list (BadNumberBoxes), each
/// row's write (ApplyToNode / Snapshot) and the red border and line under a box while typing all
/// read these, so the three can't drift apart.
/// <list type="bullet">
/// <item>A count (XP, currencies, bestiary points, mount levels): a whole number from 0 to 2,147,483,647 (the game keeps them as 32-bit ints).</item>
/// <item>A raw appearance value: any whole number, negative too.</item>
/// </list>
/// </summary>
public static partial class SaveNumberRules
{
    /// <summary>A count the save can hold: a whole number, 0 or more.</summary>
    public static bool TryParseCount(string? text, out int value) => int.TryParse(text, out value) && value >= 0;

    /// <summary>A raw appearance value: any 32-bit whole number.</summary>
    public static bool TryParseWhole(string? text, out int value) => int.TryParse(text, out value);

    /// <summary>Null when Save can write <paramref name="text"/> as a count; otherwise the short line shown under the box.</summary>
    public static string? CountProblem(string? text) =>
        TryParseCount(text, out _) ? null
        : LooksLikeANumber(text) ? "0 to 2,147,483,647"
        : "Whole number, digits only";

    /// <summary>Null when Save can write <paramref name="text"/> as an appearance value; otherwise the short line shown under the box.</summary>
    public static string? WholeProblem(string? text) =>
        TryParseWhole(text, out _) ? null
        : LooksLikeANumber(text) ? "-2,147,483,648 to 2,147,483,647"
        : "Whole number, digits only";

    /// <summary>Digits (with a sign) that only fail for being out of range: "-5", "9999999999".</summary>
    private static bool LooksLikeANumber(string? text) => text is not null && SignedDigits().IsMatch(text.Trim());

    [GeneratedRegex(@"^[+-]?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SignedDigits();
}
