using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    /// <summary>
    /// Regression: the installed merge's manifest lists an EXMOD by its declared name, so a renamed
    /// mod (ticked and installed) kept reading "Needs Merge &amp; Install" and never left the pending bar.
    /// </summary>
    [Fact]
    public void InstallStatus_ARenamedExmod_IsInGameByItsDeclaredName()
    {
        var installed = new HashSet<string>(["MkHaters Bags"], StringComparer.OrdinalIgnoreCase);

        Assert.True(LibraryViewModel.IsInInstalledMerge(false, [], "MkHaters Bags", "Bags (mine)", "MkHaters_Bags", installed));
        Assert.False(LibraryViewModel.IsInInstalledMerge(false, [], "Other Mod", "Bags (mine)", "MkHaters_Bags", installed));
        // An opaque pak is still matched by its pak file names only.
        Assert.True(LibraryViewModel.IsInInstalledMerge(true, ["MkHaters Bags.pak"], null, "Renamed", "Folder", installed));
    }
}
