using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Researched 2026-10-23 against the game's real tables (3.0.33).</summary>
public sealed class SaveGameNamesTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string relative, string json)
    {
        var path = Path.Combine(_data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>Seven creatures (Bear_Cub, Kea, Roat…) leave TotalPointsRequired out and take the table's Defaults 1000; they showed "/ 0".</summary>
    [Fact]
    public void Bestiary_RowsTakeTheTablesDefaults()
    {
        Write(@"Bestiary\D_BestiaryData.json", """
            { "Defaults": { "TotalPointsRequired": 1000, "Image": "None" },
              "Rows": [ { "Name": "Bear_Cub" }, { "Name": "Wolf", "TotalPointsRequired": 3000 } ] }
            """);
        var names = new SaveGameNames(_data);

        Assert.Equal(1000, names.BestiaryCreatures["Bear_Cub"].PointsRequired);
        Assert.Null(names.BestiaryCreatures["Bear_Cub"].ImagePath); // "None" is no image
        Assert.Equal(3000, names.BestiaryCreatures["Wolf"].PointsRequired);
    }

    /// <summary>All 329 workshop research rows have no name of their own and showed as "Workshop_Envirosuit_6"; the name is the item they research.</summary>
    [Fact]
    public void WorkshopTalents_AreNamedAfterTheItemTheyResearch()
    {
        Write(@"Talents\D_Talents.json", """
            { "Rows": [ { "Name": "Workshop_Envirosuit_6", "ExtraData": { "RowName": "Meta_Envirosuit3", "DataTableName": "D_WorkshopItems" }, "TalentTree": { "RowName": "Workshop_Envirosuits" } } ] }
            """);
        Write(@"MetaWorkshop\D_WorkshopItems.json", """
            { "Rows": [ { "Name": "Meta_Envirosuit3", "Item": { "RowName": "Envirosuit_Tier3" } } ] }
            """);
        Write(@"Items\D_ItemTemplate.json", """
            { "Rows": [ { "Name": "Envirosuit_Tier3", "ItemStaticData": { "RowName": "Envirosuit_Static3" } } ] }
            """);
        Write(@"Items\D_ItemsStatic.json", """
            { "Rows": [ { "Name": "Envirosuit_Static3", "Itemable": { "RowName": "Item_Envirosuit3" } } ] }
            """);
        Write(@"Traits\D_Itemable.json", """
            { "Rows": [ { "Name": "Item_Envirosuit3", "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit3-DisplayName\", \"Composite Envirosuit\")" } ] }
            """);

        Assert.Equal("Composite Envirosuit", new SaveGameNames(_data).Talents["Workshop_Envirosuit_6"].DisplayName);
    }

    /// <summary>Blueprint talents showed row names ("Caveworm_Knife"); their name is their item's (ExtraData -> D_Itemable).</summary>
    [Fact]
    public void BlueprintTalent_IsNamedAfterItsItem()
    {
        Write(@"Talents\D_Talents.json", """
            { "Rows": [ { "Name": "Caveworm_Knife", "ExtraData": { "RowName": "Item_Caveworm_Knife", "DataTableName": "D_Itemable" } } ] }
            """);
        Write(@"Traits\D_Itemable.json", """
            { "Rows": [ { "Name": "Item_Caveworm_Knife", "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"x\", \"Caveworm Knife\")" } ] }
            """);

        Assert.Equal("Caveworm Knife", new SaveGameNames(_data).Talents["Caveworm_Knife"].DisplayName);
    }

    /// <summary>Accolade names showed their escapes: "Rock \'n\' Roll" (2026-10-23).</summary>
    [Fact]
    public void LocalizedText_IsUnescaped()
    {
        Write(@"Accolades\D_Accolades.json", """
            { "Rows": [ { "Name": "RockNRoll", "DisplayName": "NSLOCTEXT(\"D_Accolades\", \"x\", \"Rock \\'n\\' Roll\")",
                          "Description": "NSLOCTEXT(\"D_Accolades\", \"y\", \"Say \\\"hi\\\"\\\\now\")" } ] }
            """);

        var accolade = new SaveGameNames(_data).Accolades["RockNRoll"];

        Assert.Equal("Rock 'n' Roll", accolade.DisplayName);
        Assert.Equal("Say \"hi\"\\now", accolade.Description);
    }

    /// <summary>Researched 2026-10-23: what each flag is for, from the tables' own fields.</summary>
    [Fact]
    public void Flags_AreClassifiedByWhatTheyDo()
    {
        Write(@"Flags\D_AccountFlags.json", """
            { "Defaults": { "BlueprintUnlocks": [], "WorkshopUnlocks": [], "TalentModifierUnlocks": [], "RewardedFromMissions": [] },
              "Rows": [
                { "Name": "GrantedBlueprint_Bait", "BlueprintUnlocks": [ { "RowName": "Bait", "DataTableName": "D_ItemTemplate" } ],
                  "RewardedFromMissions": [ { "RowName": "Tier3_RiverLands_Extermination_0", "DataTableName": "D_ProspectList" } ] },
                { "Name": "GrantedTalent_Nullsector_Story", "TalentModifierUnlocks": [ { "RowName": "NullsectorStoryTalents" } ] },
                { "Name": "Has_Seen_NewGame_Prompt" },
                { "Name": "Styx_Level_Boost_Claimed" } ] }
            """);
        Write(@"Talents\D_PlayerTalentModifiers.json", """
            { "Rows": [ { "Name": "NullsectorStoryTalents", "TalentPointModifier": 3, "RequiredFlags": [ { "DataTableName": "D_AccountFlags", "RowName": "GrantedTalent_Nullsector_Story" } ] } ] }
            """);
        Write(@"Prospects\D_ProspectList.json", """
            { "Rows": [ { "Name": "Tier3_RiverLands_Extermination_0", "DropName": "NSLOCTEXT(\"D_ProspectList\", \"x\", \"Riverlands Extermination\")" } ] }
            """);
        Write(@"Items\D_ItemTemplate.json", """{ "Rows": [ { "Name": "Bait", "ItemStaticData": { "RowName": "Bait_Static" } } ] }""");
        Write(@"Items\D_ItemsStatic.json", """{ "Rows": [ { "Name": "Bait_Static", "Itemable": { "RowName": "Item_Bait" } } ] }""");
        Write(@"Traits\D_Itemable.json", """{ "Rows": [ { "Name": "Item_Bait", "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"x\", \"Meat Bait\")" } ] }""");
        Write(@"Flags\D_CharacterFlags.json", """
            { "Defaults": { "Description": "" },
              "Rows": [ { "Name": "TestDataFlag" }, { "Name": "Talent_Wood_Sticks" }, { "Name": "Talent_RepairBench" },
                        { "Name": "Unlocked_Bait", "Metadata": { "bIsDeprecated": true } },
                        { "Name": "StyxBlocker_B_TooltipOnly" }, { "Name": "Communicator_T2" } ] }
            """);
        Write(@"Talents\D_Talents.json", """
            { "Rows": [ { "Name": "Wood_Sticks", "DisplayName": "NSLOCTEXT(\"D_Talents\", \"x\", \"Stick Collector\")",
                          "Rewards": [ { "GrantedFlags": [ { "RowName": "Talent_Wood_Sticks", "DataTableName": "D_CharacterFlags" } ] } ] },
                        { "Name": "Prospect_RL_Extermination", "ExtraData": { "RowName": "Tier3_RiverLands_Extermination_0", "DataTableName": "D_ProspectList" } } ] }
            """);
        var names = new SaveGameNames(_data);

        var bait = names.AccountFlagInfo(0);
        Assert.Equal(FlagKind.Reward, bait.Kind);
        Assert.Equal("Bait", bait.Display);
        Assert.Equal("Unlocks Meat Bait · get it by finishing the mission Riverlands Extermination", bait.Detail);
        Assert.Equal(FlagKind.TalentPoints, names.AccountFlagInfo(1).Kind);
        Assert.StartsWith("+3 talent points", names.AccountFlagInfo(1).Detail);
        Assert.Equal(FlagKind.Prompt, names.AccountFlagInfo(2).Kind);
        Assert.Equal(FlagKind.Marker, names.AccountFlagInfo(3).Kind);
        Assert.Equal(FlagKind.Unknown, names.AccountFlagInfo(99).Kind);

        Assert.Equal([FlagKind.Unused, FlagKind.FromTalent, FlagKind.Unused, FlagKind.Unused, FlagKind.Unused, FlagKind.Marker],
            Enumerable.Range(0, 6).Select(i => names.CharacterFlagInfo(i).Kind));
        Assert.Contains("Stick Collector", names.CharacterFlagInfo(1).Detail);

        // The mission row in the save -> the reward its completion gives.
        Assert.Equal(["GrantedBlueprint_Bait"], names.MissionRewardFlags["Prospect_RL_Extermination"]);
    }

    /// <summary>Warm reads each table once and keeps what it built; only Invalidate (Refresh game data) reads the folder again.</summary>
    [Fact]
    public void Warm_KeepsTheNames_UntilInvalidate()
    {
        Write(@"Talents\D_Talents.json", """
            { "Rows": [ { "Name": "Wood_Sticks", "TalentTree": { "RowName": "Survival_Resources" } } ] }
            """);
        Write(@"Items\D_ItemsStatic.json", """
            { "Rows": [ { "Name": "Wood" } ] }
            """);
        var names = new SaveGameNames(_data);

        names.Warm();
        Directory.Delete(_data, recursive: true);

        Assert.True(names.Talents.ContainsKey("Wood_Sticks"));
        Assert.Contains("Wood", names.RowNameSet("Items/D_ItemsStatic.json"));
        names.Invalidate();
        Assert.Empty(names.Talents);
        Assert.Empty(names.RowNameSet("Items/D_ItemsStatic.json"));
    }

    /// <summary>The page warms the names in the background while SaveModSources reads them on another thread.</summary>
    [Fact]
    public async Task Lookups_FromSeveralThreadsAtOnce_AllSeeTheSameTable()
    {
        Write(@"Talents\D_Talents.json", """
            { "Rows": [ { "Name": "A" }, { "Name": "B" }, { "Name": "C" } ] }
            """);
        var names = new SaveGameNames(_data);

        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            if (i % 2 == 0)
            {
                names.Warm();
            }

            return names.Talents.Count + names.RowNameSet("Talents/D_Talents.json").Count;
        })));

        Assert.All(counts, c => Assert.Equal(6, c));
    }

    /// <summary>D_PlayerTalentModifiers and D_Talents rows that leave a field out take the table's Defaults, as the game does.</summary>
    [Fact]
    public void TalentPointsAndGrantedFlags_TakeTheTablesDefaults()
    {
        Write(@"Talents\D_PlayerTalentModifiers.json", """
            { "Defaults": { "TalentPointModifier": 2 },
              "Rows": [ { "Name": "Story", "RequiredFlags": [ { "RowName": "GrantedTalent_Story" } ] } ] }
            """);
        Write(@"Talents\D_Talents.json", """
            { "Defaults": { "Rewards": [ { "GrantedFlags": [ { "RowName": "Talent_Default_Flag" } ] } ] },
              "Rows": [ { "Name": "Plain_Talent" } ] }
            """);
        Write(@"Flags\D_CharacterFlags.json", """
            { "Rows": [ { "Name": "Talent_Default_Flag" } ] }
            """);
        var names = new SaveGameNames(_data);

        Assert.Equal(2, names.TalentPointModifiers["GrantedTalent_Story"]);
        Assert.Equal(FlagKind.FromTalent, names.CharacterFlagInfo(0).Kind);
    }
}
