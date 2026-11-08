using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Free Nexus accounts can't download from inside an app: Update sends them to the file's page instead of a file list Nexus then refuses.</summary>
public sealed partial class DownloadsViewModelTests
{
    private static NexusUserInfo Account(bool premium) => new(1, "player", premium, IsSupporter: false, "player@example.com", "https://example.com/player");

    /// <summary>Mod 400 with file 10 installed and file 30 its newer version.</summary>
    private Harness ModWithAnUpdate(bool? premium)
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: true) };
        harness.NexusApi.User = premium is { } p ? Account(p) : null;
        harness.NexusApi.FileSet = new NexusModFileSet([NexusFile(20), NexusFile(30)], [new NexusFileUpdate(10, 30)]);
        Directory.CreateDirectory(Path.Combine(_root, "Staged_UE4SS", "Main", "Scripts"));
        harness.Ue4ssMeta.Save("Main", new Ue4ssModMeta { NexusModId = 400, NexusFileId = 10 });
        return harness;
    }

    /// <summary>Regression: a free account's Update showed the file list, then Nexus refused the download and the mod's main page opened.</summary>
    [Fact]
    public async Task Update_AccountThatIsntPremium_OpensTheNewerFilesPageWithoutTheFileList()
    {
        var harness = ModWithAnUpdate(premium: false);
        var vm = harness.Build();
        var opened = new List<string>();
        vm.OpenUrl = url => { opened.Add(url); return null; };

        await vm.ResolvePrimaryFileAndFetchAsync(400, "Main");

        Assert.Equal(0, harness.Dialogs!.PickNexusFileCalls);
        Assert.Equal(0, harness.NexusApi.GetDownloadLinksCalls);
        Assert.Equal(["https://www.nexusmods.com/icarus/mods/400?tab=files&file_id=30&nmm=1"], opened);
        Assert.Contains("Nexus only lets Premium accounts download from inside an app", vm.PendingDownloadStatusMessage);
        Assert.False(vm.IsPremium);
    }

    [Fact]
    public async Task Update_PremiumAccount_ShowsTheFileList()
    {
        var harness = ModWithAnUpdate(premium: true);
        var vm = harness.Build();
        vm.OpenUrl = _ => null;

        await vm.ResolvePrimaryFileAndFetchAsync(400, "Main");

        Assert.Equal(1, harness.Dialogs!.PickNexusFileCalls);
        Assert.True(vm.IsPremium);
    }

    /// <summary>Unknown (the key couldn't be checked): the usual file list.</summary>
    [Fact]
    public async Task Update_PremiumUnknown_ShowsTheFileList()
    {
        var harness = ModWithAnUpdate(premium: null);
        var vm = harness.Build();
        vm.OpenUrl = _ => null;

        await vm.ResolvePrimaryFileAndFetchAsync(400, "Main");

        Assert.Equal(1, harness.Dialogs!.PickNexusFileCalls);
        Assert.Null(vm.IsPremium);
    }

    /// <summary>The account is looked up once per sign-in, and again after signing in with another key.</summary>
    [Fact]
    public async Task IsPremium_IsCheckedOncePerSignIn()
    {
        var harness = ModWithAnUpdate(premium: false);
        var vm = harness.Build();
        vm.OpenUrl = _ => null;

        await vm.ResolvePrimaryFileAndFetchAsync(400, "Main");
        await vm.ResolvePrimaryFileAndFetchAsync(400, "Main");
        Assert.Equal(1, harness.NexusApi.ValidateKeyCalls);

        harness.NexusApi.User = Account(premium: true);
        WeakReferenceMessenger.Default.Send(new NexusAccountChangedMessage());
        Assert.Null(vm.IsPremium);
        await vm.ResolvePrimaryFileAndFetchAsync(400, "Main");

        Assert.Equal(2, harness.NexusApi.ValidateKeyCalls);
        Assert.Equal(1, harness.Dialogs!.PickNexusFileCalls);
    }

    /// <summary>Regression: Nexus refusing a download opened the mod's main page, not the file's, so the player had to find the file again.</summary>
    [Fact]
    public async Task Download_RefusedByNexus_OpensThatFilesPage()
    {
        var harness = new Harness(_root);
        harness.NexusApi.DownloadLinksError = new NexusDownloadRejectedException("Nexus only gives download links to Premium accounts.");
        var vm = harness.Build();
        var opened = new List<string>();
        vm.OpenUrl = url => { opened.Add(url); return null; };

        await vm.FetchAndDownloadAsync("nxm://icarus/mods/12/files/34");

        Assert.Equal(["https://www.nexusmods.com/icarus/mods/12?tab=files&file_id=34&nmm=1"], opened);
        Assert.Contains("Mod Manager Download", vm.PendingDownloadStatusMessage);
    }
}
