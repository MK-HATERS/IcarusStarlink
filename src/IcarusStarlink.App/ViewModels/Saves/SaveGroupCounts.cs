namespace IcarusStarlink.App.ViewModels;

/// <summary>The count beside a grouped list's heading on the Saves page ("9 of 14 complete"), worked out from the rows the group lists.</summary>
public static class SaveGroupCounts
{
    public static string Describe(IEnumerable<object> rows)
    {
        var list = rows.ToList();
        return list.FirstOrDefault() switch
        {
            null => "",
            SaveTalentViewModel t when SavesViewModel.IsMission(t) => Of(list.OfType<SaveTalentViewModel>().Count(r => r.Rank > 0), list.Count, "done"),
            SaveTalentViewModel t when IsResearch(t) => Of(list.OfType<SaveTalentViewModel>().Count(r => r.Rank > 0), list.Count, "researched"),
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

    private static bool IsResearch(SaveTalentViewModel t) =>
        t.Model == "Workshop" || t.RowName.StartsWith("Workshop_", StringComparison.OrdinalIgnoreCase);

    private static string Of(int done, int total, string word, bool allSaysJustCount = false) =>
        allSaysJustCount && done == total ? $"{done:N0} {word}" : $"{done:N0} of {total:N0} {word}";
}
