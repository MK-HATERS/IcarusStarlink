using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// "Remove the DLC requirement" on the Editor page. The game gates a DLC item on several of its
/// rows (its D_ItemsStatic row, rows it links to such as Itemable, its D_ItemTemplate rows, the
/// recipes that make it, its workshop entry); the choice takes the gate off every one of them that
/// has it, as sparse edits of the Metadata field like any other change, and puts the game's back.
/// </summary>
public static partial class GameItemEditor
{
    private const string ItemTemplate = "D_ItemTemplate";

    /// <summary>The DLC card for an item a paid DLC gates in the game (null for any other item): one tick, on when the mod already took the gate off.</summary>
    private static EditableSection? DlcSectionOf(GameTableIndex game, ExmodPackage? mod, GameItemLookup lookup, string itemName)
    {
        var gated = DlcGatedRows(game, mod, lookup, itemName);
        if (gated.Count == 0)
        {
            return null;
        }

        var removed = gated.All(g => DlcGate.Of(Effective(game, mod, g.File, g.Row)) is null);
        var name = DlcGate.Name(game, gated[0].Level);
        var field = new EditableField(new EditPath(DlcGate.MetadataField, []), "Remove the DLC requirement",
            $"On = players without {name} can use it too. Off = it needs {name}, as in the game. Rows shared with other items are unlocked for those items too.",
            EditableKind.Toggle, JsonValue.Create(false), JsonValue.Create(removed), false);
        return new EditableSection(DlcSection, $"DLC: {name}", ItemsStatic, itemName, itemName, 1, [field], []);
    }

    /// <summary>
    /// Every row the item uses whose game row a paid DLC gates: the file, the row as the item uses
    /// it now (an own copy's name), the game's row, the DLC, and the D_ItemsStatic field that links
    /// to it (null for rows not reached through a link).
    /// </summary>
    private static List<(string File, string Row, string GameRow, string Level, string? Field)> DlcGatedRows(GameTableIndex game, ExmodPackage? mod, GameItemLookup lookup, string itemName)
    {
        var rows = new List<(string File, string Row, string GameRow, string Level, string? Field)>();
        if (game.Row(ItemsStatic, itemName) is not { } gameStatic)
        {
            return rows;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string file, string row, string gameRow, string? field = null)
        {
            if (seen.Add($"{file}|{row}") && DlcGate.Of(game.Row(file, gameRow)) is { } level)
            {
                rows.Add((file, row, gameRow, level, field));
            }
        }

        Add(ItemsStatic, itemName, itemName);
        foreach (var (field, value) in Effective(game, mod, ItemsStatic, itemName) ?? gameStatic)
        {
            if (RowLinks.LinkedRow(value) is { } rowName && game.LinkTarget(ItemsStatic, field) is { } table && game.CurrentFileFor(table) is { } file)
            {
                Add(file, rowName, game.GameRowName(file, rowName) ?? RowLinks.RowNameOf(gameStatic[field]) ?? rowName, field);
            }
        }

        var products = game.ProductNamesFor(itemName);
        if (game.CurrentFileFor(ItemTemplate) is { } templates)
        {
            foreach (var product in products)
            {
                Add(templates, product, product);
            }
        }

        foreach (var recipe in lookup.RecipesMaking(products))
        {
            Add(Recipes, recipe, recipe);
        }

        if (lookup.WorkshopFor(products) is { } workshop)
        {
            Add(WorkshopItems, workshop, workshop);
        }

        return rows;
    }

    /// <summary>
    /// Takes the gate off every gated row of the item (<paramref name="remove"/>), or puts the game's
    /// Metadata back on them. A game row other items link to too changes for them as well; its line says so.
    /// </summary>
    private static void SetDlcGate(
        GameTableIndex game, ExmodPackage mod, GameItemLookup lookup, string itemName, bool remove, List<string> lines, List<(string File, string Row, string Top)> written)
    {
        foreach (var (file, row, gameRow, level, field) in DlcGatedRows(game, mod, lookup, itemName))
        {
            var metadata = Effective(game, mod, file, row)?[DlcGate.MetadataField];
            if (remove == (DlcGate.OfMetadata(metadata) is null))
            {
                continue; // already as asked
            }

            SetValue(game, mod, file, row, gameRow, new EditPath(DlcGate.MetadataField, []), remove ? DlcGate.Without(metadata) : null);
            written.Add((file, row, DlcGate.MetadataField));
            var users = field is not null && game.HasRow(file, row) ? lookup.UsersOf(field, row) : 1;
            var shared = users > 1 ? $"  (all {users:N0} items)" : "";
            lines.Add($"{GameTableIndex.TableNameOf(file)} › {row} · needs {DlcGate.Name(game, level)}: {(remove ? "no longer" : "again, as in the game")}{shared}");
        }
    }
}
