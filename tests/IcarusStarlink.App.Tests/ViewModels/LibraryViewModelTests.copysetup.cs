using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    // ---- Library › Check › Copy my setup ----

    [Fact]
    public async Task CopyMySetup_CopiesTheModSetupAsMarkdown_AndSaysSo()
    {
        var harness = new TestHarness();
        var settings = new AppSettings();
        harness.ModSetupReport = new ModSetupReport(() => [harness.AddMod("Better_Bags", version: "1.2", nexusModId: 123)], () => settings);
        var vm = harness.Build();
        string? copied = null;
        vm.CopyToClipboard = text => copied = text;

        await vm.CopyMySetupCommand.ExecuteAsync(null);

        Assert.NotNull(copied);
        Assert.StartsWith("## My Icarus mod setup", copied);
        Assert.Contains("**Library mods**", copied);
        Assert.Contains(@"- Better\_Bags 1.2", copied);
        Assert.Contains("Nexus mod 123", copied);
        Assert.Contains("Copied your mod setup", vm.StatusMessage);
    }

    [Fact]
    public async Task CopyMySetup_ClipboardBusy_SaysItCouldntCopy()
    {
        var harness = new TestHarness();
        var settings = new AppSettings();
        harness.ModSetupReport = new ModSetupReport(() => [], () => settings);
        var vm = harness.Build();
        vm.CopyToClipboard = _ => throw new InvalidOperationException("The clipboard is in use.");

        await vm.CopyMySetupCommand.ExecuteAsync(null);

        Assert.Contains("Couldn't copy your mod setup", vm.StatusMessage);
        Assert.Contains("The clipboard is in use.", vm.StatusMessage);
    }
}
