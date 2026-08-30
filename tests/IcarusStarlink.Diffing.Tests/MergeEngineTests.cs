using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

public class MergeEngineTests
{
    private static FieldChange ScalarChange(string item, string field, int value) =>
        new("Items-D_ItemsStatic.json", item, field, OriginalValue: null, JsonValue.Create(value), ValueSemantic.Scalar);

    [Fact]
    public void Merge_TwoModsSameField_LastInQueueWins()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 30) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal(30, change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void Merge_ModsTouchingDifferentFields_KeepsBoth()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Weight", 1) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void Merge_ThreeWayConflict_ManualPickOverridesRegistryForThatField()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modC = new List<FieldChange> { ScalarChange("Sword", "Damage", 30) };

        var key = ("Items-D_ItemsStatic.json", "Sword", "Damage");
        var manualPicks = new Dictionary<(string, string, string), int> { [key] = 1 }; // pick modB, not the last

        var resolved = MergeEngine.Merge([modA, modB, modC], new MergeRuleRegistry(), manualPicks);

        var change = Assert.Single(resolved);
        Assert.Equal(20, change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void Merge_GameplayTagQueryFields_CombineInsteadOfOverwriting()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery),
        };
        var modB = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Resources"]"""), ValueSemantic.GameplayTagQuery),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        var tags = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Tools", "Resources"], tags);
    }

    [Fact]
    public void Merge_GameplayTagQuery_OneModRemovesField_DefersToLastWriteWinsInsteadOfCorruptingArray()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery),
        };
        var modB = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: JsonNode.Parse("""["Tools"]"""), NewValue: null, ValueSemantic.GameplayTagQuery,
                IsFieldRemoved: true),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public void Merge_GameplayTagQuery_SingleModTouchesField_PassesThroughUnwrapped()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonValue.Create("Tools"), ValueSemantic.GameplayTagQuery),
        };

        var resolved = MergeEngine.Merge([modA], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal("Tools", change.NewValue!.GetValue<string>());
    }

    [Fact]
    public void Merge_GameplayTagQuery_MixedSemanticsAcrossMods_DoesNotMisfireCombine()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery),
        };
        var modB = new List<FieldChange>
        {
            // A different mod's value for the same field name happened to classify differently
            // (e.g. structurally shaped like a row reference) — the combine rule must not fire.
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""{"RowName": "X"}"""), ValueSemantic.RowReference),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal("X", change.NewValue!["RowName"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_LastWriteWins_IsNewItemIsOredAcrossCandidates_EvenWhenWinnerSaysOtherwise()
    {
        // ModA was diffed while the row didn't exist yet (IsNewItem=true); ModB was diffed later,
        // after some other change added the row (IsNewItem=false) and wins last-write-wins. The
        // resolved change must still say IsNewItem=true so TableApplier creates the row instead
        // of skipping it if the row is in fact absent from whatever base gets applied to.
        var modA = new FieldChange(
            "Items-D_ItemsStatic.json", "SpecialItem", "Damage",
            OriginalValue: null, JsonValue.Create(10), ValueSemantic.Scalar, IsNewItem: true);
        var modB = new FieldChange(
            "Items-D_ItemsStatic.json", "SpecialItem", "Damage",
            OriginalValue: JsonValue.Create(10), JsonValue.Create(20), ValueSemantic.Scalar, IsNewItem: false);

        var resolved = MergeEngine.Merge([[modA], [modB]], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal(20, change.NewValue!.GetValue<int>()); // modB's value won
        Assert.True(change.IsNewItem); // but new-item status is still honored
    }

    [Fact]
    public void Merge_GameplayTagQueryCombine_IsNewItemIsOredAcrossCandidates()
    {
        var modA = new FieldChange(
            "Deployables-D_DeployableSetup.json", "SpecialBench", "UnlockTagQuery",
            OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery, IsNewItem: true);
        var modB = new FieldChange(
            "Deployables-D_DeployableSetup.json", "SpecialBench", "UnlockTagQuery",
            OriginalValue: null, JsonNode.Parse("""["Resources"]"""), ValueSemantic.GameplayTagQuery, IsNewItem: false);

        var resolved = MergeEngine.Merge([[modA], [modB]], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.True(change.IsNewItem);
    }

    [Fact]
    public void Merge_ManualPickIndexOutOfRange_ThrowsClearException()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };

        var key = ("Items-D_ItemsStatic.json", "Sword", "Damage");
        var manualPicks = new Dictionary<(string, string, string), int> { [key] = 5 }; // stale pick

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MergeEngine.Merge([modA, modB], new MergeRuleRegistry(), manualPicks));
        Assert.Contains("Sword", ex.Message);
        Assert.Contains("Damage", ex.Message);
    }

    [Fact]
    public void Merge_TwoModsSameFieldDifferentCurrentFileCasing_StillConflictResolvedTogether()
    {
        // Two mods' own EXMOD files disagree on casing for the exact same real Windows path
        // ("Items-D_ItemsStatic.json" vs "items-d_itemsstatic.json") — different extraction tools
        // aren't guaranteed to agree. Both must land in the same conflict group, not be treated as
        // two independent, unrelated files.
        var modA = new List<FieldChange> { new("Items-D_ItemsStatic.json", "Sword", "Damage", null, JsonValue.Create(20), ValueSemantic.Scalar) };
        var modB = new List<FieldChange> { new("items-d_itemsstatic.json", "Sword", "Damage", null, JsonValue.Create(30), ValueSemantic.Scalar) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal(30, change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void Merge_GameplayTagQuery_DuplicateEntriesAreNotRepeated()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery),
        };
        var modB = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools", "Resources"]"""), ValueSemantic.GameplayTagQuery),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        var tags = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Tools", "Resources"], tags);
    }

    [Fact]
    public void FindConflicts_TwoModsDifferentValues_ReturnsOneConflictWithBothCandidatesInQueueOrder()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };

        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA, modB]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("Sword", conflict.ItemName);
        Assert.Equal("Damage", conflict.FieldName);
        Assert.Equal(2, conflict.Candidates.Count);
        Assert.Equal("Mod A", conflict.Candidates[0].ModName);
        Assert.Equal(10, conflict.Candidates[0].Change.NewValue!.GetValue<int>());
        Assert.Equal("Mod B", conflict.Candidates[1].ModName);
        Assert.Equal(20, conflict.Candidates[1].Change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void FindConflicts_TwoModsSameValue_ReturnsNoConflict()
    {
        // Both mods happen to set the field to the identical value — nothing for a human to pick
        // between, so this shouldn't be surfaced as something needing a decision.
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };

        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA, modB]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_SingleModTouchesField_ReturnsNoConflict()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };

        var conflicts = MergeEngine.FindConflicts(["Mod A"], [modA]);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_ThreeMods_OnlyTwoTouchTheSameField_CandidatesExcludeTheThird()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Weight", 1) }; // different field, not part of the conflict
        var modC = new List<FieldChange> { ScalarChange("Sword", "Damage", 30) };

        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B", "Mod C"], [modA, modB, modC]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(2, conflict.Candidates.Count);
        Assert.Equal("Mod A", conflict.Candidates[0].ModName);
        Assert.Equal("Mod C", conflict.Candidates[1].ModName);
    }

    [Fact]
    public void FindConflicts_MismatchedListLengths_Throws()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };

        Assert.Throws<ArgumentException>(() => MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA]));
    }

    [Fact]
    public void GroupConflictsByMod_TwoModsConflict_EachNamesTheOther()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA, modB]);

        var byMod = MergeEngine.GroupConflictsByMod(conflicts);

        Assert.Equal(["Mod B"], byMod["Mod A"]);
        Assert.Equal(["Mod A"], byMod["Mod B"]);
    }

    [Fact]
    public void GroupConflictsByMod_ModConflictsWithDifferentModsOnDifferentFields_BothNamed()
    {
        // Mod A conflicts with Mod B on Damage and with Mod C on Weight — a real shape this
        // aggregation exists to handle: one mod's own "conflicts with" set can span several
        // different mods across several different fields, not just one.
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10), ScalarChange("Sword", "Weight", 1) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modC = new List<FieldChange> { ScalarChange("Sword", "Weight", 2) };
        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B", "Mod C"], [modA, modB, modC]);

        var byMod = MergeEngine.GroupConflictsByMod(conflicts);

        Assert.Equal(["Mod B", "Mod C"], byMod["Mod A"].OrderBy(n => n));
        Assert.Equal(["Mod A"], byMod["Mod B"]);
        Assert.Equal(["Mod A"], byMod["Mod C"]);
    }

    [Fact]
    public void GroupConflictsByMod_NoConflicts_ReturnsEmpty()
    {
        var byMod = MergeEngine.GroupConflictsByMod([]);

        Assert.Empty(byMod);
    }

    private static IReadOnlyDictionary<string, JsonObject> BaseTables(string currentFile, string item, string field, int baseValue) =>
        new Dictionary<string, JsonObject> { [currentFile] = new JsonObject { [item] = new JsonObject { [field] = JsonValue.Create(baseValue) } } };

    [Fact]
    public void Merge_LaterCandidateEqualsBaseValue_DoesNotClobberAnEarlierGenuineEdit()
    {
        // The whole-row-copy scenario found in real data: modB's own copy of this field never
        // actually changed anything (it matches base exactly) — it must not "win" over modA's real
        // edit just because it's later in the queue.
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var baseTables = BaseTables("Items-D_ItemsStatic.json", "Sword", "Damage", baseValue: 10);

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry(), baseTablesByFile: baseTables);

        var change = Assert.Single(resolved);
        Assert.Equal(20, change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void Merge_OnlyCandidateEqualsBaseValue_ProducesNoResolvedChangeAtAll()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var baseTables = BaseTables("Items-D_ItemsStatic.json", "Sword", "Damage", baseValue: 10);

        var resolved = MergeEngine.Merge([modA], new MergeRuleRegistry(), baseTablesByFile: baseTables);

        Assert.Empty(resolved);
    }

    [Fact]
    public void Merge_NoBaseTablesGiven_BaseEqualCandidateStillWinsAsBefore()
    {
        // baseTablesByFile is opt-in — omitting it must reproduce the exact pre-existing behavior
        // (plain last-write-wins, no filtering), for every caller that doesn't pass it.
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal(10, change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void FindConflicts_OneCandidateEqualsBaseValue_IsExcludedSoNoConflictIsReported()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var baseTables = BaseTables("Items-D_ItemsStatic.json", "Sword", "Damage", baseValue: 10);

        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA, modB], baseTables);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_BothCandidatesDifferFromBase_StillReportsARealConflict()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 30) };
        var baseTables = BaseTables("Items-D_ItemsStatic.json", "Sword", "Damage", baseValue: 10);

        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA, modB], baseTables);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(2, conflict.Candidates.Count);
    }

    [Fact]
    public void FindConflicts_BaseValueIsExplicitJsonNull_StillFiltersACandidateThatAlsoMatchesNull()
    {
        // A base field can legitimately be JSON null (e.g. an optional reference), not merely
        // absent — TryGetPropertyValue returns true with a null value in both cases, so the
        // filtering logic must check field PRESENCE, not just "is the returned value non-null".
        var modA = new List<FieldChange> { ScalarChange("Sword", "Enchantment", 20) };
        modA[0] = modA[0] with { NewValue = null };
        var baseTables = new Dictionary<string, JsonObject>
        {
            ["Items-D_ItemsStatic.json"] = new JsonObject { ["Sword"] = new JsonObject { ["Enchantment"] = null } },
        };

        var modB = new List<FieldChange> { ScalarChange("Sword", "Enchantment", 99) };
        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA, modB], baseTables);

        // Mod A's null matches base's explicit null (filtered out); Mod B's real value doesn't
        // (kept) — so there's exactly one real remaining candidate, not a conflict.
        Assert.Empty(conflicts);
    }

    [Fact]
    public void FindConflicts_NoBaseValueKnownForThisField_NotFiltered()
    {
        // baseTablesByFile given, but this particular file/item/field isn't in it (e.g. a
        // genuinely new item) — nothing to compare against, so no filtering happens for this key.
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var baseTables = new Dictionary<string, JsonObject> { ["SomeOtherFile.json"] = new JsonObject() };

        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA, modB], baseTables);

        Assert.Single(conflicts);
    }

    [Fact]
    public void FindConflicts_CandidateIndexAlignsWithMergeManualPicksIndex()
    {
        // The whole point of Candidates' ordering: a UI can pick Candidates[i] here and pass that
        // same i as Merge's own manualPicks index, for the identical orderedModChanges.
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modC = new List<FieldChange> { ScalarChange("Sword", "Damage", 30) };
        IReadOnlyList<IReadOnlyList<FieldChange>> orderedModChanges = [modA, modB, modC];

        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B", "Mod C"], orderedModChanges);
        var conflict = Assert.Single(conflicts);
        var pickedIndex = 1; // "Mod B" per the UI's own selection

        var key = (conflict.CurrentFile, conflict.ItemName, conflict.FieldName);
        var resolved = MergeEngine.Merge(orderedModChanges, new MergeRuleRegistry(),
            new Dictionary<(string, string, string), int> { [key] = pickedIndex });

        var change = Assert.Single(resolved);
        Assert.Equal(conflict.Candidates[pickedIndex].Change.NewValue!.GetValue<int>(), change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void CountChangesDifferingFromBase_AllChangesDifferFromBase_CountsAll()
    {
        var changes = new List<FieldChange> { ScalarChange("Sword", "Damage", 30), ScalarChange("Sword", "Weight", 5) };
        var baseTables = new Dictionary<string, JsonObject>
        {
            ["Items-D_ItemsStatic.json"] = new JsonObject { ["Sword"] = new JsonObject { ["Damage"] = JsonValue.Create(10), ["Weight"] = JsonValue.Create(1) } },
        };

        Assert.Equal(2, MergeEngine.CountChangesDifferingFromBase(changes, baseTables));
    }

    [Fact]
    public void CountChangesDifferingFromBase_OneChangeMatchesBaseExactly_ExcludedFromCount()
    {
        // The real "stale whole-item-copy" scenario the field notes document: Weight is carried
        // along unchanged from base, Damage is the mod's one genuine edit.
        var changes = new List<FieldChange> { ScalarChange("Sword", "Damage", 30), ScalarChange("Sword", "Weight", 1) };
        var baseTables = new Dictionary<string, JsonObject>
        {
            ["Items-D_ItemsStatic.json"] = new JsonObject { ["Sword"] = new JsonObject { ["Damage"] = JsonValue.Create(10), ["Weight"] = JsonValue.Create(1) } },
        };

        Assert.Equal(1, MergeEngine.CountChangesDifferingFromBase(changes, baseTables));
    }

    [Fact]
    public void CountChangesDifferingFromBase_SameFieldTouchedTwiceByOneMod_CollapsesToLastValueFirst()
    {
        // A real EXMOD pattern (see the field notes): a mod's own File_Items can list the same
        // item twice with different values — the LAST entry is what TableApplier would actually
        // produce, so only that one should be judged against base, not both.
        var changes = new List<FieldChange> { ScalarChange("Sword", "Damage", 999), ScalarChange("Sword", "Damage", 10) };
        var baseTables = BaseTables("Items-D_ItemsStatic.json", "Sword", "Damage", baseValue: 10);

        Assert.Equal(0, MergeEngine.CountChangesDifferingFromBase(changes, baseTables));
    }

    [Fact]
    public void CountChangesDifferingFromBase_FieldNotInBaseTables_CountsAsDiffering()
    {
        var changes = new List<FieldChange> { ScalarChange("BrandNewItem", "Damage", 5) };
        var baseTables = new Dictionary<string, JsonObject> { ["Items-D_ItemsStatic.json"] = new JsonObject() };

        Assert.Equal(1, MergeEngine.CountChangesDifferingFromBase(changes, baseTables));
    }
}
