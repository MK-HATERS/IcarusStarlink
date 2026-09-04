using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

public class Ue4ssModRowViewModelTests
{
    private static Ue4ssModRowViewModel MakeRow(string? minUe4ssVersion, string? installedLoaderVersion) =>
        new(
            name: "SomeMod", realIsEnabled: true, isBuiltIn: false, nexusModId: null, knownVersion: null,
            minUe4ssVersion: minUe4ssVersion, installedLoaderVersion: installedLoaderVersion, onDirtyChanged: () => { });

    [Fact]
    public void HasVersionWarning_InstalledLoaderBelowDeclaredMinimum_IsTrue()
    {
        var row = MakeRow(minUe4ssVersion: "3.0.5", installedLoaderVersion: "3.0.1");

        Assert.Equal(Ue4ssVersionCompatibility.BelowMinimum, row.VersionCompatibility);
        Assert.True(row.HasVersionWarning);
        Assert.Contains("3.0.5", row.VersionWarningTooltip);
        Assert.Contains("3.0.1", row.VersionWarningTooltip);
    }

    [Fact]
    public void HasVersionWarning_InstalledLoaderAboveDeclaredMinimum_IsFalse()
    {
        var row = MakeRow(minUe4ssVersion: "3.0.1", installedLoaderVersion: "3.0.5");

        Assert.Equal(Ue4ssVersionCompatibility.Met, row.VersionCompatibility);
        Assert.False(row.HasVersionWarning);
        Assert.Null(row.VersionWarningTooltip);
    }

    [Fact]
    public void HasVersionWarning_InstalledLoaderEqualsDeclaredMinimum_IsFalse()
    {
        var row = MakeRow(minUe4ssVersion: "3.0.1", installedLoaderVersion: "3.0.1");

        Assert.False(row.HasVersionWarning);
    }

    [Fact]
    public void HasVersionWarning_NoDeclaredMinimum_IsFalseEvenWithAnOldLoader()
    {
        var row = MakeRow(minUe4ssVersion: null, installedLoaderVersion: "1.0.0");

        Assert.Equal(Ue4ssVersionCompatibility.Unknown, row.VersionCompatibility);
        Assert.False(row.HasVersionWarning);
    }

    [Fact]
    public void HasVersionWarning_LoaderNotInstalled_IsFalseRatherThanThrowing()
    {
        var row = MakeRow(minUe4ssVersion: "3.0.1", installedLoaderVersion: null);

        Assert.False(row.HasVersionWarning);
    }
}
