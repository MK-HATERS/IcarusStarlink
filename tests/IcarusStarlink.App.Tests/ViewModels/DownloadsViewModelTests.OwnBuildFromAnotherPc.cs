using System.IO;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>"IcarusStarlink.exe --import" with a build on another PC (a network share) asks first; a local build stays silent.</summary>
public sealed partial class DownloadsViewModelTests
{
    /// <summary>A build on another PC is someone else's files: --import asks first, and No imports nothing.</summary>
    [Fact]
    public async Task ImportOwnBuild_FromANetworkShare_AsksAndNoImportsNothing()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: false) };
        var vm = harness.Build();

        var status = await vm.ImportOwnBuildAsync(@"\\127.0.0.1\NoSuchShare\Build.zip");

        Assert.Contains(harness.Dialogs.ConfirmMessages, m => m.Contains("another PC"));
        Assert.Contains("Nothing was imported", status);
    }

    [Fact]
    public async Task ImportOwnBuild_FromANetworkShare_WithNobodyToAsk_IsRefused()
    {
        var vm = new Harness(_root).Build();

        var status = await vm.ImportOwnBuildAsync(@"\\127.0.0.1\NoSuchShare\Build.zip");

        Assert.Contains("Nothing was imported", status);
    }

    /// <summary>A local build stays silent: the author's build loop isn't slowed down by a question.</summary>
    [Fact]
    public async Task ImportOwnBuild_LocalPath_AsksNothing()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: false) };
        var vm = harness.Build();

        await vm.ImportOwnBuildAsync(Path.Combine(_root, "nope.zip"));

        Assert.Empty(harness.Dialogs.ConfirmMessages);
    }

    [Theory]
    [InlineData(@"\\server\share\Build.zip", true)]
    [InlineData(@"\\?\UNC\server\share\Build.zip", true)]
    [InlineData(@"\\?\C:\Builds\Build.zip", false)]
    [InlineData(@"C:\Builds\Build.zip", false)]
    public void IsOnAnotherPc_UncPathsAreRemote(string path, bool expected) =>
        Assert.Equal(expected, DownloadsViewModel.IsOnAnotherPc(path));
}
