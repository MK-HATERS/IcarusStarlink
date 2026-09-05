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
        new ArrayUnionCombineRule(),
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

    /// <summary>
    /// True when LastWriteWinsRule is what Resolve(group) would actually apply for this field —
    /// false means a combine rule (ArrayUnionCombineRule/GameplayTagQueryCombineRule) would instead
    /// union every candidate's own value, not just pick the last mod's. Lets a caller that only
    /// needs to know WHICH KIND of resolution "no manual pick" performs — not the resolved value
    /// itself — answer that without depending on which concrete IFieldMergeRule implementations
    /// exist. Built for the EXMOD editor's conflict picker: its own "Default" option used to be
    /// unconditionally labeled "last mod wins," which is wrong whenever a combine rule actually
    /// fires — a user who then manually picks one mod's value "to be safe" silently loses every
    /// OTHER mod's own clean addition that Default would otherwise have kept.
    /// </summary>
    public bool DefaultIsLastWriteWins(FieldChangeGroup group) => _rules.First(rule => rule.Applies(group)) is LastWriteWinsRule;
}
