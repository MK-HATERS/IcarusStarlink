using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing.Tests;

/// <summary>
/// The player's starting stats merge one stat at a time, as classic IMM does since 2.3.9: a mod
/// that sends 3 of the 72 stats changes those 3, and two mods changing different stats both apply.
/// </summary>
public class StatEntriesTests
{
    private const string File = "Stats-D_CharacterStartingStats.json";

    private static string Stat(string name) => $"(Value=\"{name}\")";

    private static JsonObject Game() => new()
    {
        ["Base_Stats"] = new JsonObject
        {
            ["StatsGranted"] = new JsonObject
            {
                [Stat("BaseMaximumHealth_+")] = 300,
                [Stat("BaseMaximumFallDamage_%")] = 100,
                [Stat("ChanceToResistSprain_%")] = 0,
                [Stat("BaseMovementSpeed_+")] = 355,
            },
        },
    };

    private static FieldChange Stats(JsonObject stats, string row = "Base_Stats") =>
        new(File, row, "StatsGranted", null, stats, ValueSemantic.GenericCompound, IsNewItem: true);

    private static JsonObject Merge(IReadOnlyList<IReadOnlyList<FieldChange>> mods, Dictionary<(string, string, string), int>? picks = null)
    {
        var split = mods.Select(StatEntries.Split).ToList();
        var bases = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase) { [File] = Game() };
        var resolved = MergeEngine.Merge(split, new MergeRuleRegistry(), picks, bases);
        return (JsonObject)TableApplier.Apply(Game(), resolved)["Base_Stats"]!["StatsGranted"]!;
    }

    [Fact]
    public void AModSendingSomeStats_ChangesThoseAndKeepsTheGamesOthers()
    {
        // laanp's WaypointTeleport: 3 stats, no fall damage, no sprains.
        var stats = Merge([[Stats(new JsonObject { [Stat("BaseMaximumFallDamage_%")] = 0, [Stat("ChanceToResistSprain_%")] = 100 })]]);

        Assert.Equal(300, (int)stats[Stat("BaseMaximumHealth_+")]!);
        Assert.Equal(355, (int)stats[Stat("BaseMovementSpeed_+")]!);
        Assert.Equal(0, (int)stats[Stat("BaseMaximumFallDamage_%")]!);
        Assert.Equal(100, (int)stats[Stat("ChanceToResistSprain_%")]!);
        Assert.Equal(4, stats.Count);
    }

    [Fact]
    public void TwoModsChangingDifferentStats_BothApply_AndDontConflict()
    {
        IReadOnlyList<FieldChange> noFall = [Stats(new JsonObject { [Stat("BaseMaximumFallDamage_%")] = 0 })];
        // A full copy of the list with one stat changed: the copied stats are the game's and drop out.
        IReadOnlyList<FieldChange> fast = [Stats(new JsonObject
        {
            [Stat("BaseMaximumHealth_+")] = 300, [Stat("BaseMaximumFallDamage_%")] = 100,
            [Stat("ChanceToResistSprain_%")] = 0, [Stat("BaseMovementSpeed_+")] = 600,
        })];

        var stats = Merge([noFall, fast]);
        var conflicts = MergeEngine.FindConflicts(["No fall", "Fast"], [.. new[] { noFall, fast }.Select(StatEntries.Split)],
            new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase) { [File] = Game() });

        Assert.Equal(0, (int)stats[Stat("BaseMaximumFallDamage_%")]!);
        Assert.Equal(600, (int)stats[Stat("BaseMovementSpeed_+")]!);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void TwoModsChangingTheSameStat_ConflictOnThatStat_AndAPickChoosesIt()
    {
        IReadOnlyList<FieldChange> a = [Stats(new JsonObject { [Stat("BaseMaximumHealth_+")] = 400, [Stat("ChanceToResistSprain_%")] = 50 })];
        IReadOnlyList<FieldChange> b = [Stats(new JsonObject { [Stat("BaseMaximumHealth_+")] = 900 })];
        var split = new[] { a, b }.Select(StatEntries.Split).ToList();

        var conflict = Assert.Single(MergeEngine.FindConflicts(["A", "B"], split,
            new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase) { [File] = Game() }));
        Assert.Equal("StatsGranted › BaseMaximumHealth_+", StatEntries.Display(conflict.FieldName));

        Assert.Equal(900, (int)Merge([a, b])[Stat("BaseMaximumHealth_+")]!); // the later mod by default
        var picked = Merge([a, b], new() { [(File, "Base_Stats", conflict.FieldName)] = 0 });
        Assert.Equal(400, (int)picked[Stat("BaseMaximumHealth_+")]!);
        Assert.Equal(50, (int)picked[Stat("ChanceToResistSprain_%")]!);
    }

    [Fact]
    public void AStatSetToNull_IsTakenOut_AndOtherFieldsAreNotSplit()
    {
        var stats = Merge([[Stats(new JsonObject { [Stat("BaseMovementSpeed_+")] = null })]]);
        Assert.False(stats.ContainsKey(Stat("BaseMovementSpeed_+")));

        IReadOnlyList<FieldChange> other = [new("Traits-D_Itemable.json", "Item_X", "Stats", null, new JsonObject { ["a"] = 1 }, ValueSemantic.GenericCompound)];
        Assert.Same(other, StatEntries.Split(other));
    }

    /// <summary>Regression: a mod writing "statsGranted" wasn't split per stat, so its 3 stats went in whole beside the game's 72.</summary>
    [Fact]
    public void ALowerCaseStatsGranted_IsSplitPerStat_UnderTheGamesSpelling()
    {
        var change = new FieldChange(File, "Base_Stats", "statsGranted", null, new JsonObject { [Stat("X")] = 5 }, ValueSemantic.GenericCompound);

        var split = Assert.Single(StatEntries.Split([change]));

        Assert.Equal(StatEntries.Compose("StatsGranted", Stat("X")), split.FieldName);
    }

    /// <summary>Regression: the row's own field was looked up by its exact spelling, so a second "statsgranted" list was made.</summary>
    [Fact]
    public void ALowerCaseStatsGranted_WritesIntoTheRowsOwnList()
    {
        var change = new FieldChange(File, "Base_Stats", "statsGranted", null, new JsonObject { [Stat("X")] = 5 }, ValueSemantic.GenericCompound);
        var table = new JsonObject { ["Base_Stats"] = new JsonObject { ["StatsGranted"] = new JsonObject { [Stat("X")] = 1, [Stat("Y")] = 2 } } };

        var row = TableApplier.Apply(table, StatEntries.Split([change]))["Base_Stats"]!.AsObject();

        var list = Assert.Single(row, kv => string.Equals(kv.Key, "statsgranted", StringComparison.OrdinalIgnoreCase)).Value!.AsObject();
        Assert.Equal(5, (int)list[Stat("X")]!);
        Assert.Equal(2, (int)list[Stat("Y")]!);
        Assert.Equal(2, list.Count);
    }

    /// <summary>The game's value of one stat is found whatever the row's field spelling.</summary>
    [Fact]
    public void Get_FindsTheStatInARowSpellingTheFieldInOtherCase()
    {
        var row = new JsonObject { ["statsgranted"] = new JsonObject { [Stat("X")] = 7 } };

        var (found, value) = StatEntries.Get(row, StatEntries.Compose("StatsGranted", Stat("X")));

        Assert.True(found);
        Assert.Equal(7, (int)value!);
    }
}
