using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// The talent, blueprint and workshop trees (D_Talents) as they come out of a merge: the game's
/// nodes with every queued mod's changes laid over them in queue order, checked the way the game
/// draws and unlocks them (after Wax's workshop check). Only findings that involve a node a mod
/// adds or changes are reported: the game's own trees have none of these (checked on the real
/// data: no two nodes on top of each other, no requirement across trees).
/// - Two nodes on top of each other in one tree (usually two mods, or a mod and a node the game
///   added since, using the same free spot): one hides the other.
/// - Nodes that each wait for the other (a circle): none of them can ever be unlocked.
/// - A node that needs a node from another tree: the game draws no line between trees, and
///   nothing in its own trees does it.
/// - A node in a tree neither the game nor a queued mod has: it shows nowhere.
/// </summary>
public static class TalentTreeChecker
{
    public const string TalentsFile = "Talents-D_Talents.json";
    public const string TreesFile = "Talents-D_TalentTrees.json";

    public enum FindingKind
    {
        Overlap,
        Circle,
        OtherTree,
        MissingTree,
    }

    /// <param name="Mods">The queued mods the finding involves, in queue order ("the game" is never one).</param>
    public sealed record Finding(FindingKind Kind, string Node, string Tree, IReadOnlyList<string> Mods, string Reason);

    /// <param name="Mods">The queued mods that add or change this node.</param>
    /// <param name="ModFields">The fields those mods set; every field for a node a mod adds.</param>
    private sealed record Node(string Name, JsonObject Row, List<string> Mods, HashSet<string> ModFields, bool IsNew)
    {
        /// <summary>A mod decides where it is: it adds the node, or sets where it sits or how big it is.</summary>
        public bool PlacedByMod => IsNew || ModFields.Overlaps(["Position", "Size", "TalentTree", "TalentType"]);

        /// <summary>A mod decides what it needs or which tree it's in.</summary>
        public bool LinkedByMod => IsNew || ModFields.Overlaps(["RequiredTalents", "TalentTree"]);
    }

    public static IReadOnlyList<Finding> Check(IReadOnlyList<(string ModName, ExmodPackage Package)> queue, GameTableIndex game)
    {
        var defaults = game.Defaults(TalentsFile) ?? [];
        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, row) in game.Rows(TalentsFile) ?? [])
        {
            if (row is JsonObject obj)
            {
                nodes[name] = new Node(name, (JsonObject)obj.DeepClone(), [], new HashSet<string>(StringComparer.OrdinalIgnoreCase), IsNew: false);
            }
        }

        var trees = new HashSet<string>((game.Rows(TreesFile) ?? []).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var (modName, package) in queue)
        {
            foreach (var file in package.Rows)
            {
                var isTalents = string.Equals(file.CurrentFile, TalentsFile, StringComparison.OrdinalIgnoreCase);
                if (string.Equals(file.CurrentFile, TreesFile, StringComparison.OrdinalIgnoreCase))
                {
                    trees.UnionWith(file.FileItems.Select(i => i.Name));
                }

                if (!isTalents)
                {
                    continue;
                }

                foreach (var item in file.FileItems)
                {
                    if (!nodes.TryGetValue(item.Name, out var node))
                    {
                        nodes[item.Name] = node = new Node(item.Name, [], [], new HashSet<string>(StringComparer.OrdinalIgnoreCase), IsNew: true);
                    }

                    foreach (var (field, value) in item.Fields)
                    {
                        node.ModFields.Add(field);
                        if (value is null)
                        {
                            node.Row.Remove(field); // taken out: the table's default applies
                        }
                        else
                        {
                            node.Row[field] = value.DeepClone();
                        }
                    }

                    if (!node.Mods.Contains(modName))
                    {
                        node.Mods.Add(modName);
                    }
                }
            }
        }

        var findings = new List<Finding>();
        var byTree = nodes.Values.GroupBy(n => TreeOf(n, defaults), StringComparer.OrdinalIgnoreCase);
        foreach (var tree in byTree)
        {
            // Reroutes are the small joints lines bend through, not nodes anyone sees.
            var shown = tree.Where(n => !IsReroute(n, defaults)).ToList();
            for (var i = 0; i < shown.Count; i++)
            {
                for (var j = i + 1; j < shown.Count; j++)
                {
                    var (a, b) = (shown[i], shown[j]);
                    if (!a.PlacedByMod && !b.PlacedByMod)
                    {
                        continue;
                    }

                    var (ax, ay, aw, ah) = Box(a, defaults);
                    var (bx, by, bw, bh) = Box(b, defaults);
                    if (Math.Abs(ax - bx) < (aw + bw) / 2 && Math.Abs(ay - by) < (ah + bh) / 2)
                    {
                        findings.Add(new Finding(FindingKind.Overlap, a.Name, tree.Key, Owners(a, b),
                            $"{a.Name} ({Who(a)}) at {ax:0},{ay:0} and {b.Name} ({Who(b)}) at {bx:0},{by:0} lie on top of each other in {tree.Key}: one hides the other in game. "
                            + "Move one of them in the mod (its Position)."));
                    }
                }
            }
        }

        foreach (var node in nodes.Values.Where(n => n.Mods.Count > 0))
        {
            var tree = TreeOf(node, defaults);
            if (node.PlacedByMod && !trees.Contains(tree))
            {
                findings.Add(new Finding(FindingKind.MissingTree, node.Name, tree, node.Mods,
                    $"{node.Name} ({Who(node)}) is in the tree '{tree}', which neither the game nor a queued mod has, so it shows nowhere."));
            }

            foreach (var required in node.LinkedByMod ? Requires(node) : [])
            {
                if (nodes.TryGetValue(required, out var other) && !string.Equals(TreeOf(other, defaults), tree, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new Finding(FindingKind.OtherTree, node.Name, tree, node.Mods,
                        $"{node.Name} ({Who(node)}) needs {other.Name}, which is in another tree ({TreeOf(other, defaults)}). The game draws no line between trees and none of its own nodes does this, "
                        + "so players won't see what unlocks it."));
                }
            }
        }

        findings.AddRange(Circles(nodes, defaults));
        return findings;
    }

    /// <summary>Requirement circles that a mod's node is part of: none of their nodes can ever be unlocked.</summary>
    private static IEnumerable<Finding> Circles(Dictionary<string, Node> nodes, JsonObject defaults)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var found = new List<Finding>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in nodes.Keys)
        {
            if (state.ContainsKey(start))
            {
                continue;
            }

            // Depth-first without recursion: (node, its requirements, next one to look at).
            var stack = new List<(string Name, List<string> Next, int At)> { (start, Requires(nodes[start]).ToList(), 0) };
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (name, next, at) = stack[^1];
                if (at >= next.Count)
                {
                    state[name] = 2;
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                stack[^1] = (name, next, at + 1);
                if (!nodes.TryGetValue(next[at], out var target))
                {
                    continue;
                }

                if (state.GetValueOrDefault(target.Name) == 1)
                {
                    var from = stack.FindIndex(s => string.Equals(s.Name, target.Name, StringComparison.OrdinalIgnoreCase));
                    var circle = stack.Skip(from).Select(s => nodes[s.Name]).ToList();
                    if (circle.Any(n => n.LinkedByMod) && reported.Add(string.Join("|", circle.Select(n => n.Name).Order(StringComparer.OrdinalIgnoreCase))))
                    {
                        var mods = circle.SelectMany(n => n.Mods).Distinct().ToList();
                        found.Add(new Finding(FindingKind.Circle, circle[0].Name, TreeOf(circle[0], defaults), mods,
                            $"These nodes each wait for the next, so none of them can ever be unlocked: {string.Join(" → ", circle.Select(n => n.Name))} → {circle[0].Name}."));
                    }
                }
                else if (!state.ContainsKey(target.Name))
                {
                    state[target.Name] = 1;
                    stack.Add((target.Name, Requires(target).ToList(), 0));
                }
            }
        }

        return found;
    }

    private static IReadOnlyList<string> Owners(Node a, Node b) => [.. a.Mods.Concat(b.Mods).Distinct()];

    private static string Who(Node node) => node.Mods.Count == 0 ? "the game" : string.Join(", ", node.Mods);

    private static IEnumerable<string> Requires(Node node) =>
        (node.Row["RequiredTalents"] as JsonArray ?? [])
            .Select(e => Text((e as JsonObject)?["RowName"]))
            .Where(r => r is { Length: > 0 } && !string.Equals(r, "None", StringComparison.OrdinalIgnoreCase))!;

    private static string TreeOf(Node node, JsonObject defaults) =>
        Text(((node.Row["TalentTree"] ?? defaults["TalentTree"]) as JsonObject)?["RowName"]) ?? "None";

    private static bool IsReroute(Node node, JsonObject defaults) =>
        string.Equals(Text(node.Row["TalentType"] ?? defaults["TalentType"]), "Reroute", StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static (double X, double Y, double W, double H) Box(Node node, JsonObject defaults)
    {
        var position = (node.Row["Position"] ?? defaults["Position"]) as JsonObject;
        var size = (node.Row["Size"] ?? defaults["Size"]) as JsonObject;
        return (Number(position?["X"]), Number(position?["Y"]), Number(size?["X"], 64), Number(size?["Y"], 64));
    }

    /// <summary>A number however it's held (parsed from a file, or an int or float the editor put in).</summary>
    private static double Number(JsonNode? value, double otherwise = 0) =>
        value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : otherwise;
}
