using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

public enum BulkOperation
{
    /// <summary>Every value becomes the number typed.</summary>
    Set,

    /// <summary>Every value is multiplied by it (2 = double, 0.5 = half).</summary>
    Multiply,

    /// <summary>The number is added to every value (negative takes away).</summary>
    Add,
}

/// <summary>A choice in the bulk edit's "How" drop-down.</summary>
public sealed record BulkOperationChoice(BulkOperation Operation, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The same value on many items, for a bulk edit: a card kind (the D_ItemsStatic link field, Buff,
/// Recipe, Workshop) and a path in its row, a list position read as "every entry" (a recipe's
/// ingredients are different things on each item, but "every ingredient amount" means the same).
/// </summary>
public sealed record BulkFieldKey(string SectionKind, string Path)
{
    public static BulkFieldKey Of(string sectionKey, EditPath path) =>
        new(SectionKindOf(sectionKey), string.Join('/', new object[] { path.Top }.Concat(path.Steps).Select(s => s is int ? "*" : s.ToString())));

    /// <summary>"Recipe:Bread" → "Recipe": one card kind on every item.</summary>
    public static string SectionKindOf(string sectionKey) => sectionKey.Split(':')[0];

    /// <summary>A whole list's amounts ("Inputs/*/Count"): one bulk field covers every entry.</summary>
    public bool IsEveryEntry => Path.Contains("/*/", StringComparison.Ordinal);
}

/// <summary>A field the ticked items share, in the bulk edit's "What" drop-down.</summary>
public sealed record BulkFieldChoice(BulkFieldKey Key, string Label, int ItemCount)
{
    public string Display => $"{Label}  ·  {ItemCount:N0} item{(ItemCount == 1 ? "" : "s")}";

    public override string ToString() => Display;
}

/// <summary>A chip for a value people often change on many items: ticks the listed items that have the card (<paramref name="Has"/>, a GameItemCatalog.HasFilters key) and picks the value.</summary>
public sealed record EditorBulkShortcut(string Label, string Has, BulkFieldKey Field)
{
    public override string ToString() => Label;
}

/// <summary>The arithmetic and words of a bulk edit, kept apart from the page so they can be tested alone.</summary>
public static class BulkEdit
{
    public static readonly IReadOnlyList<BulkOperationChoice> Operations =
    [
        new(BulkOperation.Multiply, "Multiply by"),
        new(BulkOperation.Set, "Set to"),
        new(BulkOperation.Add, "Add"),
    ];

    /// <summary>Real cards and fields, checked against the game data (as GameItemEditor shows them).</summary>
    public static readonly IReadOnlyList<EditorBulkShortcut> Shortcuts =
    [
        new("Stack size", "Itemable", new("Itemable", "MaxStack")),
        new("Weight", "Itemable", new("Itemable", "Weight")),
        new("Durability", "Durable", new("Durable", "Max_Durability")),
        new("Spoil time", "Decayable", new("Decayable", "SpoilTime")),
        new("Buff length", "Consumable", new("Consumable", "Modifier/ModifierLifetime")),
        new("Storage slots", "Inventory", new(GameItemEditor.StorageSection, "StartingSlots")),
        new("Ingredients", GameItemCatalog.RecipeKey, new(GameItemEditor.RecipeSection, "Inputs/*/Count")),
        new("Research cost", GameItemCatalog.WorkshopKey, new(GameItemEditor.WorkshopSection, "ResearchCost/*/Amount")),
    ];

    /// <summary>The quick buttons beside the number: multiply by these.</summary>
    public static readonly IReadOnlyList<string> QuickMultipliers = ["0.5", "1.5", "2", "3"];

    /// <summary>
    /// Reads a preview row's typed new value(s): one number, or one per value for a list ("2, 1, 5"
    /// for three ingredients). Whole-number fields refuse fractions, as the cards do.
    /// </summary>
    public static bool TryReadValues(string text, IReadOnlyList<bool> wholeNumbers, out IReadOnlyList<double> values, out string? error)
    {
        values = [];
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != wholeNumbers.Count)
        {
            error = wholeNumbers.Count == 1 ? "Needs one number." : $"Needs {wholeNumbers.Count} numbers, one per value, with commas between.";
            return false;
        }

        var read = new List<double>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (!EditorValueText.TryParseNumber(parts[i], out var number))
            {
                error = $"'{parts[i]}' isn't a number.";
                return false;
            }

            if (wholeNumbers[i] && number != Math.Floor(number))
            {
                error = "Whole numbers only here.";
                return false;
            }

            read.Add(number);
        }

        values = read;
        error = null;
        return true;
    }

    /// <summary>
    /// The new value. A whole-number field is rounded, and a multiply or add never takes a value of
    /// 1 or more below 1 (half of 1 ingredient is still 1, not 0: zero is only ever typed on purpose
    /// with Set). Null when the result isn't a finite number.
    /// </summary>
    public static double? Apply(BulkOperation operation, double amount, double current, bool wholeNumber)
    {
        var result = operation switch
        {
            BulkOperation.Set => amount,
            BulkOperation.Multiply => current * amount,
            _ => current + amount,
        };

        if (!double.IsFinite(result))
        {
            return null;
        }

        if (wholeNumber)
        {
            result = Math.Round(result, MidpointRounding.AwayFromZero);
        }

        if (operation != BulkOperation.Set && current >= 1 && result < 1)
        {
            result = 1;
        }

        return result;
    }

    /// <summary>The drop-down's words: the card, then the value ("Durability · Max durability"); a whole list reads "every ingredient amount".</summary>
    public static string LabelOf(BulkFieldKey key, string cardTitle, string fieldLabel)
    {
        var card = key.SectionKind == GameItemEditor.RecipeSection ? "Recipe" : cardTitle;
        if (!key.IsEveryEntry)
        {
            return $"{card} · {fieldLabel}";
        }

        var list = key.Path.Split('/')[0];
        return list switch
        {
            "Inputs" => $"{card} · every ingredient amount",
            "Outputs" => $"{card} · how many one craft makes",
            "ResearchCost" => $"{card} · every research cost",
            "ReplicationCost" => $"{card} · every replication cost",
            "ItemsForRepair" => $"{card} · every repair cost",
            _ => $"{card} · every {GameItemEditor.FieldText(list, isNumber: false).Label.ToLowerInvariant()} amount",
        };
    }
}
