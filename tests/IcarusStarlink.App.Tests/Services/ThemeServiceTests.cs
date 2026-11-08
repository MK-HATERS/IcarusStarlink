using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Skins;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Reading a custom skin's colours: a hand-edited or shared skin file is untrusted text.</summary>
public sealed class ThemeServiceTests
{
    /// <summary>
    /// "sc#1" typed in a skin colour box (or written in the skin file) threw out of TryParseColor:
    /// Save skin closed Starlink, and with the Custom theme on every start failed.
    /// </summary>
    [Theory]
    [InlineData("sc#")]
    [InlineData("sc#1")]
    [InlineData("sc#1,0")]
    public void TryParseColor_UnfinishedScRgb_IsFalseNotAThrow(string text) =>
        Assert.False(ThemeService.TryParseColor(text, out _));

    [Fact]
    public void TryParseColor_ContextColor_IsRefused() =>
        Assert.False(ThemeService.TryParseColor("ContextColor file:///C:/nope.icc 1,0,0,0", out _));

    [Theory]
    [InlineData("#FF0000")]
    [InlineData("#80FF0000")]
    [InlineData("#F00")]
    [InlineData("Red")]
    [InlineData(" #00FF00 ")]
    public void TryParseColor_WhatSkinsHoldToday_StillParses(string text) =>
        Assert.True(ThemeService.TryParseColor(text, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryParseColor_Nothing_IsFalse(string? text) =>
        Assert.False(ThemeService.TryParseColor(text!, out _));

    /// <summary>
    /// A skin file reading {"Colors": null} threw while the Custom theme was applied at startup:
    /// every launch ended with "couldn't start up", and Settings (where it is fixed) was out of reach.
    /// </summary>
    [Fact]
    public void SkinColorsToApply_NullColors_IsEmpty() =>
        Assert.Empty(ThemeService.SkinColorsToApply(new CustomSkin { Colors = null! }, ["AccentBrush"]));

    /// <summary>
    /// Every key in the file went into the theme: a hand-added key named like a style replaced that
    /// style with a brush. Only the theme's own colour keys, with values that parse, are taken.
    /// </summary>
    [Fact]
    public void SkinColorsToApply_KeepsOnlyKnownColourKeys()
    {
        var skin = new CustomSkin
        {
            Colors = new() { ["AccentBrush"] = "#FF0000", ["PrimaryButtonStyle"] = "#00FF00", ["BorderBrush"] = "sc#1" },
        };

        var applied = ThemeService.SkinColorsToApply(skin, ["AccentBrush", "BorderBrush"]);

        var only = Assert.Single(applied);
        Assert.Equal("AccentBrush", only.Key);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xFF, 0, 0), only.Value);
    }
}
