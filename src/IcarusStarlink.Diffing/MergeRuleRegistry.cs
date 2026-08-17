namespace IcarusStarlink.Diffing;

/// <summary>
/// Ordered list of rules, most-specific first; the first rule whose Applies() returns true wins.
/// Adding a new combinable field type later means writing one new IFieldMergeRule and
/// registering it here — never touching the resolution logic itself.
/// </summary>
public sealed class MergeRuleRegistry(IEnumerable<IFieldMergeRule>? rules = null)
{
    private static readonly IFieldMergeRule[] DefaultRules =
    [
        new GameplayTagQueryCombineRule(),
        new LastWriteWinsRule(),
    ];

    private readonly IReadOnlyList<IFieldMergeRule> _rules = rules?.ToList() ?? [.. DefaultRules];

    public FieldChange Resolve(FieldChangeGroup group)
    {
        foreach (var rule in _rules)
        {
            if (!rule.Applies(group))
            {
                continue;
            }

            var resolved = rule.Resolve(group);

            // A resolved change counts as "new item" if ANY contributing mod's diff considered
            // it one — not just whichever candidate the rule happened to return. Mods can be
            // diffed against base snapshots taken at different times (see TableApplier's
            // docstring), so inheriting IsNewItem from a single candidate risks a legitimately
            // new row being skipped-with-warning instead of created at apply time. Enforced here,
            // once, so no IFieldMergeRule implementation has to get this right on its own.
            var isNewItem = group.OrderedChanges.Any(c => c.IsNewItem);
            return resolved.IsNewItem == isNewItem ? resolved : resolved with { IsNewItem = isNewItem };
        }

        throw new InvalidOperationException(
            $"No merge rule applies to {group.CurrentFile}:{group.ItemName}.{group.FieldName} " +
            "— LastWriteWinsRule should always be registered as a fallback.");
    }
}
