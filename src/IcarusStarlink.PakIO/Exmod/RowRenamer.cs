using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>What renaming a row did: the row's new name and how many links in the mod now point at it.</summary>
public sealed record RowRenameResult(string NewName, int LinksMoved);

/// <summary>
/// "Rename this row" in the mod editor: gives a row the mod adds itself a new name and re-points
/// every link to it inside the mod (the same links Check references reads: {"RowName": old} whose
/// table is the row's own, by its DataTableName or the table's Defaults). Only rows the mod adds:
/// a mod's copy of a GAME row is found by its name, so renaming it would add a new row and leave
/// the game's untouched.
/// </summary>
public static partial class RowRenamer
{
    /// <summary>Why this row can't be renamed at all (a game row, or a table the game data doesn't have), whatever the new name; null when it can.</summary>
    public static string? RowProblem(GameTableIndex game, string currentFile, string oldName)
    {
        if (game.Rows(currentFile) is null)
        {
            return $"{GameTableIndex.TableNameOf(currentFile)} isn't a game table this PC has (run Refresh game data in Settings), so a clash with a game row can't be checked.";
        }

        if (game.GameRowName(currentFile, oldName) is { } gameRow)
        {
            return $"'{gameRow}' is the game's own row: this mod changes it by using its name. Renaming would add a new row and leave the game's as it is. Use Duplicate (Ctrl+D) to make a new row from it instead.";
        }

        return null;
    }

    /// <summary>Why <paramref name="newName"/> can't be the row's new name, in plain words; null when it can.</summary>
    public static string? Problem(ExmodPackage package, GameTableIndex game, string currentFile, string oldName, string newName)
    {
        if (RowProblem(game, currentFile, oldName) is { } rowProblem)
        {
            return rowProblem;
        }

        if (newName.Length == 0)
        {
            return "Type a name.";
        }

        if (!RowNamePattern().IsMatch(newName))
        {
            return "Use letters, digits and _ only (game row names look like Item_Dragon_Suit).";
        }

        if (string.Equals(newName, "None", StringComparison.OrdinalIgnoreCase))
        {
            return "'None' means \"no row\" in a link, so a row can't be called that.";
        }

        if (string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            return "That's its name already.";
        }

        // A different case is the same row in game (names ignore case): allowed, it only fixes the spelling.
        if (!string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase))
        {
            if (game.GameRowName(currentFile, newName) is { } gameRow)
            {
                return $"The game already has a row '{gameRow}' in {GameTableIndex.TableNameOf(currentFile)}. Pick another name.";
            }

            var table = GameTableIndex.TableNameOf(currentFile);
            if (package.Rows.Where(r => string.Equals(GameTableIndex.TableNameOf(r.CurrentFile), table, StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.FileItems).FirstOrDefault(i => string.Equals(i.Name, newName, StringComparison.OrdinalIgnoreCase)) is { } modRow)
            {
                return $"This mod already has a row '{modRow.Name}' in {table}. Pick another name.";
            }
        }

        return null;
    }

    /// <summary>Renames the row and re-points the mod's links to it. Throws when Problem has an answer (check it first).</summary>
    public static RowRenameResult Rename(ExmodPackage package, GameTableIndex game, string currentFile, string oldName, string newName)
    {
        newName = newName.Trim();
        if (Problem(package, game, currentFile, oldName, newName) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var item = package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, currentFile, StringComparison.OrdinalIgnoreCase))
            ?.FileItems.FirstOrDefault(i => string.Equals(i.Name, oldName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"This mod has no row '{oldName}' in {currentFile}.");

        var table = GameTableIndex.TableNameOf(currentFile);
        var moved = 0;
        ReferenceChecker.VisitLinks(package, game, link =>
        {
            if (string.Equals(link.Table, table, StringComparison.OrdinalIgnoreCase) && string.Equals(link.RowName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                link.Node["RowName"] = newName;
                moved++;
            }
        });

        item.Name = newName;
        return new RowRenameResult(newName, moved);
    }

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex RowNamePattern();
}
