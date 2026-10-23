using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.ViewModels;

public enum SaveCheckSeverity
{
    /// <summary>Something the game can't have made: a value out of range.</summary>
    Problem,

    /// <summary>Two parts of the save disagree; the game may show or behave oddly.</summary>
    Warning,

    /// <summary>Worth knowing, harmless.</summary>
    Note,
}

/// <param name="Area">"MKHATERS", "Account", "Bestiary", "Mounts".</param>
/// <param name="FixLabel">"Turn on Caveworm Melee"; null when there's no sure fix.</param>
public sealed record SaveCheckFinding(SaveCheckSeverity Severity, string Area, string Message, string? FixLabel = null, Action? Fix = null)
{
    public bool CanFix => Fix is not null;

    /// <summary>A DLC / Supporters Edition note: in Fix all only with its switch on.</summary>
    public bool IsDlc => Area == SaveChecker.DlcArea;

    public string SeverityLabel => Severity switch
    {
        SaveCheckSeverity.Problem => "Problem",
        SaveCheckSeverity.Warning => "Warning",
        _ => "Note",
    };
}

/// <summary>
/// The Saves page's save check (plan step 3, approved 2026-10-23), from the rules researched on the
/// user's save: values the game can't have made, parts that disagree (a learned talent whose unlock
/// is off, a finished mission whose reward is off), and harmless oddities. It reads the editor's
/// current state, unsaved edits included, so a check before Save checks what Save would write.
/// DLC and Supporter Edition content is checked against the cached DLC list as notes only.
/// A fix only ever makes an ordinary edit, saved with Save like any other.
/// </summary>
public static class SaveChecker
{
    public const string DlcArea = "Owned DLC";

    public static IReadOnlyList<SaveCheckFinding> Check(SavesViewModel page, SaveGameNames names, string? slotFolder)
    {
        var findings = new List<SaveCheckFinding>();
        var rules = names.TalentRules;
        var accountFlags = ByRow(page.AccountFlags);

        // --- Characters ---
        foreach (var character in page.Characters)
        {
            var area = character.Name;
            if (long.TryParse(character.XpText, out var xp) && xp < 0)
            {
                findings.Add(new(SaveCheckSeverity.Problem, area, $"Negative XP ({xp:N0}).", "Set XP to 0", () => character.XpText = "0"));
            }

            var flags = ByRow(character.Flags);
            var learned = character.Talents.Where(t => t.Rank > 0).ToDictionary(t => t.RowName, StringComparer.OrdinalIgnoreCase);
            var grantedByLearned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var talent in learned.Values)
            {
                AddRankFinding(findings, area, talent);
                if (rules.GetValueOrDefault(talent.RowName) is not { } rule)
                {
                    continue;
                }

                foreach (var (table, flagRow) in rule.RequiredFlags)
                {
                    var flag = table switch
                    {
                        "D_AccountFlags" => accountFlags.GetValueOrDefault(flagRow),
                        "D_CharacterFlags" => flags.GetValueOrDefault(flagRow),
                        _ => null, // D_DLCPackageData: never checked
                    };
                    if (flag is { IsUnlocked: false })
                    {
                        var where = table == "D_AccountFlags" ? "the account's unlock" : "the character's flag";
                        findings.Add(new(SaveCheckSeverity.Warning, area,
                            $"{talent.DisplayName} is learned, but {where} it needs ({flag.Name}) is off.",
                            $"Turn on {flag.Name}", () => flag.IsUnlocked = true));
                    }
                }

                var missing = rule.RequiredTalents
                    .Where(r => !learned.ContainsKey(r) && rules.GetValueOrDefault(r) is not { IsFreeToReach: true })
                    .ToList();
                if (missing.Count > 0)
                {
                    findings.Add(new(SaveCheckSeverity.Note, area,
                        $"{talent.DisplayName} is learned without the talent before it ({string.Join(", ", missing.Select(m => names.Talents.GetValueOrDefault(m)?.DisplayName ?? m))}). The game keeps it."));
                }

                for (var rank = 0; rank < Math.Min(talent.Rank, rule.GrantedFlagsByRank.Count); rank++)
                {
                    foreach (var flagRow in rule.GrantedFlagsByRank[rank])
                    {
                        grantedByLearned.TryAdd(flagRow, talent.DisplayName);
                        if (flags.GetValueOrDefault(flagRow) is { IsUnlocked: false } flag)
                        {
                            findings.Add(new(SaveCheckSeverity.Warning, area,
                                $"{talent.DisplayName} is learned, but the flag it gives ({flag.Name}) is off.",
                                $"Turn on {flag.Name}", () => flag.IsUnlocked = true));
                        }
                    }
                }
            }

            foreach (var flag in character.Flags.Where(f => f.IsUnlocked && f.Kind == FlagKind.FromTalent && !grantedByLearned.ContainsKey(f.RowName)))
            {
                findings.Add(new(SaveCheckSeverity.Note, area,
                    $"The flag {flag.Name} is on, but no learned talent gives it (its talent was unlearned or removed).",
                    $"Turn off {flag.Name}", () => flag.IsUnlocked = false));
            }

            if (page.OverBudgetOf(character) is { } over)
            {
                findings.Add(new(SaveCheckSeverity.Note, area,
                    $"More ranks than points: {over}. The game keeps them, but nothing more can be bought in those trees until under (Talents tab › Change points per level)."));
            }

            foreach (var flag in character.Flags.Where(f => f.IsUnlocked && f.Kind == FlagKind.Unknown))
            {
                findings.Add(new(SaveCheckSeverity.Note, area, $"{flag.Name} isn't in the game's flag table (from a mod, or removed)."));
            }
        }

        // --- Account ---
        foreach (var currency in page.Currencies)
        {
            if (long.TryParse(currency.CountText, out var count) && count < 0)
            {
                findings.Add(new(SaveCheckSeverity.Problem, "Account", $"{currency.Label} is negative ({count:N0}).", $"Set {currency.Label} to 0", () => currency.CountText = "0"));
            }

            if (currency.Label.EndsWith("(not a game currency)", StringComparison.Ordinal))
            {
                findings.Add(new(SaveCheckSeverity.Note, "Account", $"{currency.Label}: the game has no such currency and ignores it."));
            }
        }

        foreach (var talent in page.WorkshopTalents.Where(t => t.Rank > 0))
        {
            AddRankFinding(findings, "Account", talent);
            foreach (var (table, flagRow) in rules.GetValueOrDefault(talent.RowName)?.RequiredFlags ?? [])
            {
                if (table == "D_AccountFlags" && accountFlags.GetValueOrDefault(flagRow) is { IsUnlocked: false } flag)
                {
                    findings.Add(new(SaveCheckSeverity.Warning, "Account",
                        $"{talent.DisplayName} is researched, but the unlock it needs ({flag.Name}) is off.",
                        $"Turn on {flag.Name}", () => flag.IsUnlocked = true));
                }
            }
        }

        foreach (var (mission, rewards) in page.MissionRewards)
        {
            if (mission.Rank == 0)
            {
                continue;
            }

            foreach (var reward in rewards.Where(r => !r.IsUnlocked))
            {
                findings.Add(new(SaveCheckSeverity.Warning, "Account",
                    $"The mission {mission.DisplayName} is done, but its reward {reward.Name} is off.",
                    $"Turn on {reward.Name}", () => reward.IsUnlocked = true));
            }
        }

        // --- DLC and Supporter Edition (the user's call, 2026-10-23: "it'll help everyone, and is
        //     harmless for people without the DLC"): notes only, never in Fix all, one per pack.
        //     The flags file is the game's cache; the game checks ownership itself when it starts. ---
        var packs = ByRow(page.BinaryFlags);
        if (packs.Count > 0)
        {
            var usedFromPack = new Dictionary<SaveFlagViewModel, List<string>>();
            var packLabels = new Dictionary<SaveFlagViewModel, string>();
            foreach (var talent in page.Characters.SelectMany(c => c.Talents).Concat(page.WorkshopTalents).Where(t => t.Rank > 0))
            {
                foreach (var (table, row) in rules.GetValueOrDefault(talent.RowName)?.RequiredFlags ?? [])
                {
                    if (table == "D_DLCPackageData" && packs.GetValueOrDefault(row) is { IsUnlocked: false } pack)
                    {
                        (usedFromPack.TryGetValue(pack, out var list) ? list : usedFromPack[pack] = []).Add(talent.DisplayName);
                        packLabels[pack] = names.DlcPackLabel(row);
                    }
                }
            }

            foreach (var (pack, used) in usedFromPack)
            {
                var things = used.Distinct().ToList();
                findings.Add(new(SaveCheckSeverity.Note, DlcArea,
                    $"{things.Count} learned or researched thing{(things.Count == 1 ? " is" : "s are")} for {packLabels[pack]} ({string.Join(", ", things.Take(4))}{(things.Count > 4 ? ", …" : "")}), "
                    + "which is off in the cached DLC list. The game checks DLC itself when it starts, so this only tidies the cache. Optional.",
                    $"Turn on {pack.Name}", () => pack.IsUnlocked = true));
            }
        }

        // --- Bestiary ---
        foreach (var entry in page.BestiaryEntries.Where(b => b.PointsRequired > 0 && b.EffectivePoints > b.PointsRequired))
        {
            findings.Add(new(SaveCheckSeverity.Note, "Bestiary", $"{entry.DisplayName} has {entry.EffectivePoints:N0} points, more than the {entry.PointsRequired:N0} it needs. Harmless."));
        }

        // --- Mounts (from the list itself; what's inside each mount comes with the mount editor) ---
        var mountTypes = names.MountTypeRowNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in page.Mounts)
        {
            if (mountTypes.Count > 0 && !mountTypes.Contains(mount.TypeRowName))
            {
                findings.Add(new(SaveCheckSeverity.Warning, "Mounts", $"{mount.Name}: the game has no mount type {mount.TypeRowName} (from a mod, or removed)."));
            }

            if (slotFolder is not null && SaveJson.String(mount.Node["MountIconName"]) is { Length: > 0 } icon
                && !File.Exists(Path.Combine(slotFolder, "Mounts", icon + ".exr")))
            {
                findings.Add(new(SaveCheckSeverity.Note, "Mounts", $"{mount.Name} has no portrait picture; the game makes a new one."));
            }
        }

        foreach (var mount in page.Mounts)
        {
            if (mount.InsideRefusal is { } why)
            {
                findings.Add(new(SaveCheckSeverity.Note, "Mounts", $"{mount.Name}: only its name and level in the list can be edited, because {why}."));
            }
            else if (mount.OverBudget is not null)
            {
                findings.Add(new(SaveCheckSeverity.Note, "Mounts", $"{mount.Name}: {mount.TalentSummary}. The game keeps the ranks, but nothing more can be bought until under."));
            }

            if (mount.Blob?.Name is { } blobName && blobName != mount.Name)
            {
                findings.Add(new(SaveCheckSeverity.Warning, "Mounts",
                    $"{mount.Name}: the game's own copy of its name is \"{blobName}\" (the two drifted apart; the game may show either).",
                    $"Use \"{mount.Name}\" for both", mount.SyncNameOnSave));
            }
        }

        foreach (var shared in page.Mounts.GroupBy(m => SaveJson.String(m.Node["MountIconName"]) ?? "").Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            findings.Add(new(SaveCheckSeverity.Problem, "Mounts",
                $"{string.Join(" and ", shared.Select(m => m.Name))} share one mount ID; the game may mix them up. Usually a copy made by hand."));
        }

        // --- Items ---
        foreach (var item in page.MetaInventoryItems.Where(i => i.Source?.Origin == SaveRowOrigin.Misnamed))
        {
            findings.Add(new(SaveCheckSeverity.Warning, "Items", $"{item.DisplayName}: {item.Source!.Label}"));
        }

        return [.. findings.OrderBy(f => f.Severity)];
    }

    private static void AddRankFinding(List<SaveCheckFinding> findings, string area, SaveTalentViewModel talent)
    {
        if (talent.MaxRank > 0 && talent.Rank > talent.MaxRank)
        {
            findings.Add(new(SaveCheckSeverity.Problem, area,
                $"{talent.DisplayName} is at rank {talent.Rank}; the game's highest is {talent.MaxRank}.",
                $"Set {talent.DisplayName} to rank {talent.MaxRank}", () => talent.Rank = talent.MaxRank));
        }
    }

    private static Dictionary<string, SaveFlagViewModel> ByRow(IEnumerable<SaveFlagViewModel> flags)
    {
        var byRow = new Dictionary<string, SaveFlagViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in flags)
        {
            byRow.TryAdd(flag.RowName, flag);
        }

        return byRow;
    }
}
