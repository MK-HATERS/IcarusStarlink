using System.Reflection;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class SettingsViewModelTests
{
    /// <summary>
    /// Regression: the update window was built without the download-handler registrar and the dialog
    /// service, so it never offered "Make Starlink the download handler" when Nexus links went to
    /// another app (and would have changed the registry without asking if it had).
    /// </summary>
    [Fact]
    public void UpdateWindow_GetsTheDownloadHandlerRegistrarAndTheDialogService()
    {
        using var harness = new Harness();
        var vm = harness.BuildViewModel();
        var release = new AppUpdateRelease("3.3.0", "", "", AssetDigest: null, NexusFileId: 500);

        var update = vm.CreateAppUpdateViewModel(release, nexusLink: null);

        Assert.Same(harness.NxmProtocolRegistrar, PrivateField(update, "_nxmRegistrar"));
        Assert.Same(harness.DialogService, PrivateField(update, "_dialogService"));
    }

    private static object? PrivateField(AppUpdateViewModel update, string name) =>
        typeof(AppUpdateViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(update);

    /// <summary>An unfinished scRGB colour ("sc#1") in a skin box says Not saved, the same as any other bad colour.</summary>
    [Fact]
    public void SaveSkin_UnfinishedScRgbColour_SaysNotSavedInsteadOfClosing()
    {
        using var harness = new Harness();
        harness.ThemeService.Colors = new Dictionary<string, string> { ["AccentBrush"] = "#FF8A3D" };
        var vm = harness.BuildViewModel();
        vm.SkinTokens[0].Hex = "sc#1";

        var thrown = Record.Exception(() => vm.SaveSkinCommand.Execute(null));

        Assert.Null(thrown);
        Assert.StartsWith("Not saved", vm.SkinStatusMessage);
    }
}
