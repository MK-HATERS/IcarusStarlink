namespace IcarusStarlink.App.ViewModels;

/// <summary>The count beside a grouped list's heading on the Saves page ("9 of 14 complete"), worked out from the rows the group lists.</summary>
public static class SaveGroupCounts
{
    /// <param name="wholeGroup">A talent group's every row, listed or not (the list hides unlearned ones unless asked): the count is the whole tree's.</param>
    public static string Describe(IEnumerable<object> rows, Func<string, IReadOnlyList<SaveTalentViewModel>>? wholeGroup = null)
    {
        var list = rows.ToList();
        return list.FirstOrDefault() switch
        {
            null => "",
            SaveTalentViewModel t when SavesViewModel.IsMission(t) => Of(list.OfType<SaveTalentViewModel>().Count(r => r.Rank > 0), list.Count, "done"),
            SaveTalentViewModel t when IsResearch(t) => Of(list.OfType<SaveTalentViewModel>().Count(r => r.Rank > 0), list.Count, "researched"),
            SaveTalentViewModel t when wholeGroup?.Invoke(t.Group) is { Count: > 0 } tree => DescribeTree([.. tree]),
            // Talents list only what the character has unless "Show unlearned" is on: "22 learned" then.
            SaveTalentViewModel => Of(list.OfType<SaveTalentViewModel>().Count(r => r.IsUnlockedInGame), list.Count, "learned", allSaysJustCount: true),
            SaveBestiaryEntryViewModel => Of(list.OfType<SaveBestiaryEntryViewModel>().Count(b => b.IsComplete),
                list.OfType<SaveBestiaryEntryViewModel>().Count(b => b.PointsRequired > 0), "complete"),
            SaveAccoladeLineViewModel => Of(list.OfType<SaveAccoladeLineViewModel>().Sum(l => l.Tiers.Count(t => t.Accolade.IsCompleted)),
                list.OfType<SaveAccoladeLineViewModel>().Sum(l => l.Tiers.Count), "earned"),
            SaveFlagViewModel => Of(list.OfType<SaveFlagViewModel>().Count(f => f.IsUnlocked), list.Count, "on"),
            _ => list.Count.ToString("N0"),
        };
    }

    /// <summary>"20 of 24 learned, 18 at their highest rank"; "All 24 at their highest rank" when the tree is done.</summary>
    public static string DescribeTree(IReadOnlyList<ITreeTalent> tree)
    {
        var learned = tree.Count(t => t.IsUnlockedInGame);
        var maxed = tree.Count(t => t.IsMaxed || (t.Rank == 0 && t.IsDefaultUnlocked));
        return maxed == tree.Count
            ? $"All {tree.Count:N0} at their highest rank"
            : $"{learned:N0} of {tree.Count:N0} learned, {maxed:N0} at their highest rank";
    }

    private static bool IsResearch(SaveTalentViewModel t) =>
        t.Model == "Workshop" || t.RowName.StartsWith("Workshop_", StringComparison.OrdinalIgnoreCase);

    private static string Of(int done, int total, string word, bool allSaysJustCount = false) =>
        allSaysJustCount && done == total ? $"{done:N0} {word}" : $"{done:N0} of {total:N0} {word}";
}
