using IcarusStarlink.Core.Profiles;

namespace IcarusStarlink.Core.Tests.Profiles;

public class GameplayOptionsTests
{
    [Fact]
    public void DescribeActiveOptions_NothingActive_ReturnsEmpty()
    {
        Assert.Empty(new GameplayOptions().DescribeActiveOptions());
    }

    [Fact]
    public void DescribeActiveOptions_EveryOptionCoveredByHasCategory1Active()
    {
        var options = new GameplayOptions
        {
            SpeedBoost = BoostLevel.Level1,
            PlayerBoost = BoostLevel.Level2,
            XpBoost = XpBoostLevel.Level3,
            DisableTemperatures = true,
            RemoveLevelCap = true,
        };

        var descriptions = options.DescribeActiveOptions();

        Assert.Contains("Speed Boost Level 1", descriptions);
        Assert.Contains("Player Boost Level 2", descriptions);
        Assert.Contains("XP Boost Level 3", descriptions);
        Assert.Contains("Disable Temperatures", descriptions);
        Assert.Contains("Remove Level Cap", descriptions);
        Assert.Equal(5, descriptions.Count);
    }

    [Fact]
    public void DescribeActiveOptions_EveryOptionCoveredByHasCategory2Active()
    {
        var options = new GameplayOptions
        {
            StacksMultiplier = 2,
            SlotsMultiplier = 3,
            CraftCost = CraftCostReduction.FiftyPercent,
            SpeedCraftingReductionPercent = 25,
            TamingSpeedReductionPercent = 50,
            RemoveWeight = true,
            UnlimitedAmmo = true,
        };

        var descriptions = options.DescribeActiveOptions();

        Assert.Contains("Stacks x2", descriptions);
        Assert.Contains("Slots x3", descriptions);
        Assert.Contains("Craft Cost 50%", descriptions);
        Assert.Contains("Speed Crafting 25%", descriptions);
        Assert.Contains("Faster Taming 50%", descriptions);
        Assert.Contains("Remove Weight", descriptions);
        Assert.Contains("Unlimited Ammo", descriptions);
        Assert.Equal(7, descriptions.Count);
    }

    [Fact]
    public void DescribeActiveOptions_CraftCostCreative_DescribedAsCreative()
    {
        var options = new GameplayOptions { CraftCost = CraftCostReduction.Creative };

        Assert.Contains("Craft Cost Creative (0%)", options.DescribeActiveOptions());
    }

    [Fact]
    public void DescribeActiveOptions_ZeroOrNegativeMultiplier_NotDescribed()
    {
        var options = new GameplayOptions { StacksMultiplier = 0, SlotsMultiplier = -1, SpeedCraftingReductionPercent = 0 };

        Assert.Empty(options.DescribeActiveOptions());
    }
}
