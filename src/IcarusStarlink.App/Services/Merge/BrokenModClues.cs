using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>One queued mod as the finder sees it: its EXMOD (null for a prebuilt pak, which can't be read row by row here).</summary>
public sealed record BrokenModCandidate(string FolderName, string Name, ExmodPackage? Package);

/// <summary>A game row the player says is broken ("Canteen"), with its table when picked from the game data.</summary>
/// <param name="CurrentFile">"Items-D_ItemsStatic.json"; null for a typed word that matched the name only.</param>
public sealed record BrokenModTarget(string? CurrentFile, string RowName);

/// <summary>A queued mod that touches what's broken, why in plain words, and how strongly (3 = changes it, 2 = changes something linked to it, 1 = same table).</summary>
public sealed record BrokenModSuspect(string FolderName, string Name, string Reason, int Strength);

/// <summary>
/// The finder's clues, worked out from the queued mods' EXMODs and the game data, with no WPF:
/// which mods touch what the player says is broken (suspects, tested first), and which mods need
/// another (a link to a row only that mod adds: they travel together in every test).
/// </summary>
public static class BrokenModClues
{
    /// <summary>
    /// Mod → the other candidates it needs: rows it links to that neither the game nor the mod
    /// itself has, but another queued mod adds (ModHealthChecker's own link reading, as the merge
    /// check's "Needs a mod"). Every mod that adds such a row counts, so a test never lacks it.
    /// </summary>
    public static Dictionary<string, IReadOnlyCollection<string>> Needs(IReadOnlyList<BrokenModCandidate> mods, GameTableIndex index)
    {
        var addedBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var mod in mods.Where(m => m.Package is not null))
        {
            foreach (var key in ModHealthChecker.AddedRows(mod.Package!, index))
            {
                if (!addedBy.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    addedBy[key] = set;
                }

                set.Add(mod.FolderName);
            }
        }

        var none = new HashSet<string>(StringComparer.Ordinal);
        var needs = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Where(m => m.Package is not null))
        {
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in ModHealthChecker.LinksMissingFrom(mod.Package!, index, none))
            {
                if (addedBy.TryGetValue(link.Key, out var providers))
                {
                    needed.UnionWith(providers.Where(p => !string.Equals(p, mod.FolderName, StringComparison.OrdinalIgnoreCase)));
                }
            }

            if (needed.Count > 0)
            {
                needs[mod.FolderName] = needed;
            }
        }

        return needs;
    }

    /// <summary>
    /// The queued mods that touch what's broken, strongest reason first: a mod that changes the
    /// row itself ("changes Canteen"), changes a row that uses it or that it uses ("changes a
    /// recipe that uses Canteen"), or only other rows of the same table. Without targets, none.
    /// </summary>
    public static IReadOnlyList<BrokenModSuspect> Suspects(IReadOnlyList<BrokenModCandidate> mods, IReadOnlyCollection<BrokenModTarget> targets, GameTableIndex? index)
    {
        if (targets.Count == 0)
        {
            return [];
        }

        var targetNames = targets.Select(t => t.RowName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetTables = targets.Where(t => t.CurrentFile is not null).Select(t => t.CurrentFile!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The rows the broken ones use in game (its Itemable, Consumable… rows): a mod changing one
        // of those changes how the broken thing works.
        var usedByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (index is not null)
        {
            foreach (var target in targets.Where(t => t.CurrentFile is not null))
            {
                if (index.Row(target.CurrentFile!, target.RowName) is { } row)
                {
                    foreach (var linked in LinkedRowNames(row).Where(n => !targetNames.Contains(n)))
                    {
                        usedByTarget.TryAdd(linked, target.RowName);
                    }
                }
            }
        }

        var suspects = new List<BrokenModSuspect>();
        foreach (var mod in mods.Where(m => m.Package is not null))
        {
            (int Strength, string Reason)? best = null;
            var reasons = 0;
            void Consider(int strength, string reason)
            {
                reasons++;
                if (best is null || strength > best.Value.Strength)
                {
                    best = (strength, reason);
                }
            }

            foreach (var file in mod.Package!.Rows.Where(r => !IsEndOfMod(r.CurrentFile)))
            {
                var table = GameTableIndex.TableNameOf(file.CurrentFile);
                foreach (var item in file.FileItems)
                {
                    if (targetNames.Contains(item.Name))
                    {
                        Consider(3, $"changes {item.Name} ({table})");
                        continue;
                    }

                    if (LinkedRowNames(item.Fields).FirstOrDefault(targetNames.Contains) is { } uses)
                    {
                        Consider(2, $"changes {Describe(table, item.Name)} that uses {uses}");
                        continue;
                    }

                    if (usedByTarget.TryGetValue(item.Name, out var user))
                    {
                        Consider(2, $"changes {item.Name} ({table}), which {user} uses");
                        continue;
                    }

                    if (targetTables.Contains(file.CurrentFile))
                    {
                        Consider(1, $"changes other rows in {table}");
                    }
                }
            }

            if (best is { } found)
            {
                var more = reasons > 1 && found.Strength > 1 ? $" (and {reasons - 1} more)" : "";
                suspects.Add(new BrokenModSuspect(mod.FolderName, mod.Name, found.Reason + more, found.Strength));
            }
        }

        return [.. suspects.OrderByDescending(s => s.Strength)];
    }

    /// <summary>
    /// The suspects to test first: those with a strong reason (changes it, or something linked to
    /// it); only the same-table ones when there are no strong ones. Empty when that would be every
    /// mod (then testing them first tells nothing).
    /// </summary>
    public static IReadOnlyList<BrokenModSuspect> FirstGroup(IReadOnlyList<BrokenModSuspect> suspects, int candidateCount)
    {
        var strong = suspects.Where(s => s.Strength >= 2).ToList();
        var group = strong.Count > 0 ? strong : [.. suspects];
        return group.Count >= candidateCount ? [] : group;
    }

    /// <summary>
    /// What typed words point at: every game row named with all of them ("canteen" → Canteen,
    /// Canteen_Water…), from the game data's row list, plus rows the queued mods add.
    /// At most <paramref name="limit"/>.
    /// </summary>
    public static IReadOnlyList<BrokenModTarget> TargetsFromWords(string words, IEnumerable<(string CurrentFile, string RowName)> gameRows, IReadOnlyList<BrokenModCandidate> mods, int limit = 200)
    {
        var parts = words.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.Length >= 3).ToList();
        if (parts.Count == 0)
        {
            return [];
        }

        bool Matches(string name) => parts.All(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
        var modRows = mods.Where(m => m.Package is not null)
            .SelectMany(m => m.Package!.Rows.Where(r => !IsEndOfMod(r.CurrentFile)).SelectMany(r => r.FileItems.Select(i => (r.CurrentFile, RowName: i.Name))));
        return [.. gameRows.Concat(modRows)
            .Where(r => Matches(r.RowName))
            .DistinctBy(r => (r.CurrentFile.ToLowerInvariant(), r.RowName.ToLowerInvariant()))
            .Take(limit)
            .Select(r => new BrokenModTarget(r.CurrentFile, r.RowName))];
    }

    /// <summary>The EXMOD's closing "EndOfMod" marker (PakIO's ExmodSentinelFiles, internal there): not a game table.</summary>
    private static bool IsEndOfMod(string currentFile) => string.Equals(currentFile, "EndOfMod", StringComparison.OrdinalIgnoreCase);

    /// <summary>A row in a known table in plain words ("a recipe"); otherwise its name and table.</summary>
    private static string Describe(string table, string rowName) => table switch
    {
        "D_ProcessorRecipes" => $"a recipe ({rowName})",
        "D_Talents" => $"a talent ({rowName})",
        "D_WorkshopItems" => $"a workshop item ({rowName})",
        "D_Blueprints" => $"a blueprint ({rowName})",
        "D_LootTables" or "D_LootTable" => $"a loot table ({rowName})",
        _ => $"{rowName} ({table})",
    };

    /// <summary>Every RowName a row's fields link to (any depth), "None" left out.</summary>
    private static IEnumerable<string> LinkedRowNames(IEnumerable<KeyValuePair<string, JsonNode?>> fields)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, value) in fields)
        {
            Collect(value, names);
        }

        names.Remove("None");
        return names;
    }

    private static void Collect(JsonNode? node, HashSet<string> names)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["RowName"] is JsonValue row && row.TryGetValue<string>(out var name) && name.Length > 0)
                {
                    names.Add(name);
                }

                foreach (var (_, child) in o)
                {
                    Collect(child, names);
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    Collect(child, names);
                }

                break;
        }
    }
}
