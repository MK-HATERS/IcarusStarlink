using System.IO;
using System.Text.Json;

namespace IcarusStarlink.App.Services;

public sealed partial class SaveGameNames
{
    // --- Flags ---

    /// <summary>What each account flag is for (by row index), from D_AccountFlags' own fields — researched 2026-10-23.</summary>
    public IReadOnlyList<FlagInfo> AccountFlagInfos => Cached(ref _accountFlagInfos, LoadAccountFlagInfos);

    /// <summary>What each character flag is for (by row index): every one turned out to be set by a talent, deprecated, tooltip-only, test or never granted — researched 2026-10-23.</summary>
    public IReadOnlyList<FlagInfo> CharacterFlagInfos => Cached(ref _characterFlagInfos, LoadCharacterFlagInfos);

    public FlagInfo AccountFlagInfo(int id) => id >= 0 && id < AccountFlagInfos.Count ? AccountFlagInfos[id] : new FlagInfo($"Flag {id}", $"Flag {id}", "Not in the game's flag table (from a mod, or removed).", FlagKind.Unknown);

    public FlagInfo CharacterFlagInfo(int id) => id >= 0 && id < CharacterFlagInfos.Count ? CharacterFlagInfos[id] : new FlagInfo($"Flag {id}", $"Flag {id}", "Not in the game's flag table (from a mod, or removed).", FlagKind.Unknown);

    public string CharacterFlagName(int id) => id >= 0 && id < CharacterFlagNames.Count ? CharacterFlagNames[id] : $"Flag {id}";

    public string AccountFlagName(int id) => id >= 0 && id < AccountFlagNames.Count ? AccountFlagNames[id] : $"Flag {id}";

    private static readonly string[] FlagPrefixes = ["GrantedBlueprint_", "GrantedWorkshop_", "GrantedTalent_", "Talent_", "Mission_"];

    private static string FlagDisplay(string rowName)
    {
        foreach (var prefix in FlagPrefixes)
        {
            if (rowName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && rowName.Length > prefix.Length)
            {
                return HumanizeMountType(rowName[prefix.Length..]);
            }
        }

        return HumanizeMountType(rowName);
    }

    private IReadOnlyList<FlagInfo> LoadAccountFlagInfos() => Safe<IReadOnlyList<FlagInfo>>(() =>
    {
        var result = new List<FlagInfo>();
        if (Table(AccountFlagsTable) is not { } table)
        {
            return result;
        }

        var missions = MissionNames;
        var talentPoints = TalentPointModifiers;
        var templateNames = LoadItemTemplateNames();
        var workshopNames = WorkshopItemNames;
        var defaults = table.Defaults;
        foreach (var (name, row) in RowsOf(AccountFlagsTable, keepNameless: true))
        {
            var unlocks = RowNames(row, defaults, "BlueprintUnlocks").Select(r => templateNames.GetValueOrDefault(r, HumanizeMountType(r)))
                .Concat(RowNames(row, defaults, "WorkshopUnlocks").Select(r => workshopNames.GetValueOrDefault(r, HumanizeMountType(r))))
                .Distinct()
                .ToList();
            var from = RowNames(row, defaults, "RewardedFromMissions").Select(r => missions.GetValueOrDefault(r, HumanizeMountType(r))).Distinct().ToList();
            var fromText = from.Count == 0 ? "" : $" · get it by finishing the mission {string.Join(" or ", from)}";
            var points = talentPoints.GetValueOrDefault(name);

            FlagInfo info;
            if (points != 0)
            {
                info = new FlagInfo(name, FlagDisplay(name), $"+{points} talent points{fromText}", FlagKind.TalentPoints);
            }
            else if (unlocks.Count > 0)
            {
                info = new FlagInfo(name, FlagDisplay(name), $"Unlocks {string.Join(", ", unlocks)}{fromText}", FlagKind.Reward);
            }
            else if (name.StartsWith("Has_Seen", StringComparison.OrdinalIgnoreCase))
            {
                info = new FlagInfo(name, FlagDisplay(name), "A pop-up the game has already shown. Turning it off shows it again.", FlagKind.Prompt);
            }
            else
            {
                info = new FlagInfo(name, FlagDisplay(name), "A one-off marker the game sets itself (a level boost claimed, a refund done, an upgrade). Changing it may make the game repeat or skip it.", FlagKind.Marker);
            }

            result.Add(info);
        }

        return result;
    }, () => []);

    private IReadOnlyList<FlagInfo> LoadCharacterFlagInfos() => Safe<IReadOnlyList<FlagInfo>>(() =>
    {
        var result = new List<FlagInfo>();
        if (Table(CharacterFlagsTable) is not { } table)
        {
            return result;
        }

        var grantedBy = LoadTalentGrantedFlags();
        var defaults = table.Defaults;
        foreach (var (name, row) in RowsOf(CharacterFlagsTable, keepNameless: true))
        {
            var description = ParseLocText(row, "Description", defaults);
            var deprecated = row.TryGetProperty("Metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("bIsDeprecated", out var dep) && dep.ValueKind == JsonValueKind.True;

            var info = grantedBy.TryGetValue(name, out var talent)
                ? new FlagInfo(name, FlagDisplay(name), $"From the talent {talent}: learning or unlearning the talent sets it.", FlagKind.FromTalent)
                : deprecated
                    ? new FlagInfo(name, FlagDisplay(name), "No longer used by the game (deprecated).", FlagKind.Unused)
                    : name.Contains("TooltipOnly", StringComparison.OrdinalIgnoreCase)
                        ? new FlagInfo(name, FlagDisplay(name), (description is { Length: > 0 } d ? d + ". " : "") + "Only shown in a mission's reward tooltip; nothing in the game reads it.", FlagKind.Unused)
                        : name is "TestDataFlag" or "TutorialTestFlag" or "RefundOnly_Repairable"
                            ? new FlagInfo(name, FlagDisplay(name), "A test or internal flag; nothing a player uses.", FlagKind.Unused)
                            : name.StartsWith("Talent_", StringComparison.OrdinalIgnoreCase)
                                ? new FlagInfo(name, FlagDisplay(name), "No talent grants this any more.", FlagKind.Unused)
                                : new FlagInfo(name, FlagDisplay(name), description is { Length: > 0 } text ? text : "Set by the game itself (a story step or an upgrade).", FlagKind.Marker);
            result.Add(info);
        }

        return result;
    }, () => []);

    /// <summary>D_ProspectList row -> its drop name ("ARCWOOD: Outpost"), for mission names.</summary>
    private IReadOnlyDictionary<string, string> MissionNames => Cached(ref _missionNames, () => Safe<IReadOnlyDictionary<string, string>>(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, row) in RowsOf(Path.Combine("Prospects", "D_ProspectList.json")))
        {
            if (ParseLocText(row, "DropName") is { Length: > 0 } drop)
            {
                names[rowName] = drop;
            }
        }

        return names;
    }, () => new Dictionary<string, string>()));
}
