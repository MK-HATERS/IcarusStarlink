using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>A link in a mod to a row that neither the game nor the mod has.</summary>
public sealed record BrokenReference(string CurrentFile, string RowName, string FieldPath, string TargetTable, string MissingRow)
{
    public string Describe() => $"{GameTableIndex.TableNameOf(CurrentFile)} › {RowName} › {FieldPath} points at '{MissingRow}' in {TargetTable}, which doesn't exist";
}

/// <summary>An item-trait row the game doesn't have and nothing in the mod uses — an edit of a row a game update renamed or removed, or a leftover.</summary>
public sealed record UnusedRow(string CurrentFile, string RowName)
{
    public string Describe() => $"{GameTableIndex.TableNameOf(CurrentFile)} › {RowName} isn't in the game and nothing in the mod uses it (renamed or removed by a game update?)";
}

public sealed record ReferenceReport(IReadOnlyList<BrokenReference> Broken, IReadOnlyList<UnusedRow> Unused)
{
    public bool IsClean => Broken.Count == 0 && Unused.Count == 0;
}

/// <summary>
/// "Does everything this mod links to exist?" A link ({"RowName": "X"}) to a row the game doesn't
/// have — a typo (BetterSupplies' packages use the mesh "Item_Meta_Ration"; the game's is
/// "Mesh_Meta_Ration") or a row a game update renamed — is ignored at best and breaks the item at
/// worst, with no message. The target table comes from the link's own DataTableName, else from the
/// table's Defaults (where Icarus keeps it for top-level links); rows the mod adds itself count, and
/// names compare like Unreal's do (ignoring case). Also: item-trait rows the game doesn't have that
/// nothing links to — StacksX10 still sets the weight of five items the game dropped.
/// </summary>
public static class ReferenceChecker
{
    /// <param name="assets">Reads the mod's own blueprints (they can use a row by name, which no table shows), so a row they mention isn't called unused. Only called when some row might be.</param>
    public static ReferenceReport Check(ExmodPackage package, GameTableIndex game, Func<IReadOnlyCollection<byte[]>>? assets = null)
    {
        // What the mod adds: "D_ItemsStatic" → its row names.
        var modRows = package.Rows
            .Where(r => game.Rows(r.CurrentFile) is not null)
            .GroupBy(r => GameTableIndex.TableNameOf(r.CurrentFile), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(r => r.FileItems).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var linked = new HashSet<(string Table, string Row)>(new TableRowComparer());

        var broken = new List<BrokenReference>();
        foreach (var row in package.Rows)
        {
            if (game.Rows(row.CurrentFile) is null)
            {
                continue; // not a game table (EndOfMod, or one this game version doesn't have)
            }

            var defaults = game.Defaults(row.CurrentFile);
            foreach (var item in row.FileItems)
            {
                foreach (var (field, value) in item.Fields)
                {
                    Walk(value, defaults?[field], field, topLevel: true, link =>
                    {
                        var table = link.ExplicitTable ?? (link.TopLevel ? game.LinkTarget(row.CurrentFile, field) : link.DefaultsTable);
                        if (table is null || game.CurrentFileFor(table) is not { } targetFile)
                        {
                            return; // can't tell where it points — say nothing rather than guess
                        }

                        linked.Add((table, link.RowName));
                        if (!game.HasRow(targetFile, link.RowName) && modRows.GetValueOrDefault(table)?.Contains(link.RowName) != true)
                        {
                            broken.Add(new BrokenReference(row.CurrentFile, item.Name, link.Path, table, link.RowName));
                        }
                    });
                }
            }
        }

        // Item traits only exist to be linked from an item, so an unlinked one the game lacks does nothing.
        var traitTables = TraitTables(game);
        IReadOnlyCollection<byte[]>? assetBytes = null;
        bool MentionedByAssets(string name) => MentionedIn(assetBytes ??= assets?.Invoke() ?? [], name);
        var unused = package.Rows
            .Where(r => traitTables.Contains(GameTableIndex.TableNameOf(r.CurrentFile)) && game.Rows(r.CurrentFile) is not null)
            .SelectMany(r => r.FileItems
                .Where(i => !game.HasRow(r.CurrentFile, i.Name) && !linked.Contains((GameTableIndex.TableNameOf(r.CurrentFile), i.Name)) && !MentionedByAssets(i.Name))
                .Select(i => new UnusedRow(r.CurrentFile, i.Name)))
            .ToList();

        return new ReferenceReport(broken, unused);
    }

    /// <summary>The tables D_ItemsStatic links into (D_Itemable, D_Armour, D_Consumable …), from its Defaults.</summary>
    private static HashSet<string> TraitTables(GameTableIndex game)
    {
        const string itemsStatic = "Items-D_ItemsStatic.json";
        return (game.Defaults(itemsStatic) ?? [])
            .Select(kv => game.LinkTarget(itemsStatic, kv.Key))
            .OfType<string>()
            .Where(t => !string.Equals(t, "D_ItemsStatic", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Unreal assets keep the names they use as plain ASCII in their name table.</summary>
    private static bool MentionedIn(IReadOnlyCollection<byte[]> assets, string name)
    {
        if (assets.Count == 0)
        {
            return false;
        }

        var needle = System.Text.Encoding.ASCII.GetBytes(name);
        return assets.Any(bytes => bytes.AsSpan().IndexOf(needle) >= 0);
    }

    private sealed class TableRowComparer : IEqualityComparer<(string Table, string Row)>
    {
        public bool Equals((string Table, string Row) x, (string Table, string Row) y) =>
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Row, y.Row, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Table, string Row) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Row));
    }

    private readonly record struct Link(string Path, string RowName, string? ExplicitTable, string? DefaultsTable, bool TopLevel);

    /// <summary>Visits every {"RowName": …} under node; shape is the matching part of the table's Defaults, which may name the target table.</summary>
    private static void Walk(JsonNode? node, JsonNode? shape, string path, bool topLevel, Action<Link> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["RowName"] is JsonValue v && v.TryGetValue<string>(out var rowName))
                {
                    if (rowName.Length > 0 && rowName != "None")
                    {
                        var explicitTable = obj["DataTableName"] is JsonValue t && t.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
                        var defaultsTable = (shape as JsonObject)?["DataTableName"] is JsonValue d && d.TryGetValue<string>(out var ds) && ds.Length > 0 ? ds : null;
                        visit(new Link(path, rowName, explicitTable, defaultsTable, topLevel));
                    }

                    return;
                }

                foreach (var (key, child) in obj)
                {
                    Walk(child, (shape as JsonObject)?[key], $"{path}.{key}", topLevel: false, visit);
                }

                break;
            case JsonArray array:
                var elementShape = (shape as JsonArray)?.FirstOrDefault();
                for (var i = 0; i < array.Count; i++)
                {
                    Walk(array[i], elementShape, $"{path}[{i}]", topLevel: false, visit);
                }

                break;
        }
    }
}
