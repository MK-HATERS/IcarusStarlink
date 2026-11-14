using IcarusStarlink.App.Services;
using Candidate = IcarusStarlink.App.Services.WpfDialogService.OwnerCandidate;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Which window a question opens over (WpfDialogService.PickOwner).</summary>
public sealed class WpfDialogServiceOwnerTests
{
    private static Candidate Main(bool active = false, int z = 5) => new(active, IsUsable: true, IsVisible: true, IsMain: true, z);

    private static Candidate Other(bool active = false, int z = 3, bool visible = true, bool usable = true) => new(active, usable, visible, IsMain: false, z);

    [Fact]
    public void TheActiveWindowOwnsTheDialog()
    {
        Assert.Equal(0, WpfDialogService.PickOwner([Main(active: true), Other()]));
        Assert.Equal(1, WpfDialogService.PickOwner([Main(), Other(active: true)]));
    }

    /// <summary>
    /// Regression: the user was in another program when Update everything asked its install
    /// question. No Starlink window was active, so the main window owned the question and it opened
    /// behind the Game update window, which then showed the Install step running for ever.
    /// </summary>
    [Fact]
    public void NoActiveWindow_TheWindowOverTheMainOneOwnsTheDialog_NotTheMainWindow()
    {
        Assert.Equal(1, WpfDialogService.PickOwner([Main(z: 1), Other(z: 2)]));
    }

    [Fact]
    public void NoActiveWindow_SeveralOpen_TheFrontMostOwnsTheDialog()
    {
        Assert.Equal(2, WpfDialogService.PickOwner([Main(z: 9), Other(z: 7), Other(z: 4), Other(z: 6)]));
    }

    [Fact]
    public void NoActiveWindow_OnlyTheMainWindow_OwnsTheDialog()
    {
        Assert.Equal(0, WpfDialogService.PickOwner([Main()]));
        // A hidden window isn't in front of anything.
        Assert.Equal(0, WpfDialogService.PickOwner([Main(), Other(visible: false, z: 0)]));
    }

    [Fact]
    public void AClosedWindowIsNeverTheOwner()
    {
        Assert.Equal(1, WpfDialogService.PickOwner([Other(active: true, usable: false), Main()]));
        Assert.Equal(-1, WpfDialogService.PickOwner([Other(usable: false)]));
        Assert.Equal(-1, WpfDialogService.PickOwner([]));
    }
}
