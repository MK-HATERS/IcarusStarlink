using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// CueBaseGameContentProvider wraps real CUE4Parse mounting the same way every CueUasset*Decoder
/// already does, which needs a genuine Icarus install to exercise a successful mount — not
/// available in this test project (see CueUassetDecoderTests' own doc comment for the same gap).
/// What IS safely testable here, with no real game install needed at all, is the contract every
/// caller depends on: a missing/unset IcarusContentPath, or a Paks folder that doesn't exist,
/// degrades to "base-game content unavailable" (TryLoadExport returns null) rather than throwing —
/// both during construction (this service does no I/O until the first real TryLoadExport call) and
/// during that first call itself.
/// </summary>
public class CueBaseGameContentProviderTests
{
    [Fact]
    public void Construction_NeverThrows_RegardlessOfSettingsContent()
    {
        var withNoContentPath = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings()));
        var withUnsetContentPath = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings { IcarusContentPath = null }));

        Assert.NotNull(withNoContentPath);
        Assert.NotNull(withUnsetContentPath);
    }

    [Fact]
    public void TryLoadExport_IcarusContentPathNotSet_ReturnsNullInsteadOfThrowing()
    {
        var provider = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings()));

        var result = provider.TryLoadExport<object>("Weapons/Materials/M_BaseWeapon.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryLoadExport_IcarusContentPathBlank_ReturnsNullInsteadOfThrowing()
    {
        var provider = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings { IcarusContentPath = "   " }));

        var result = provider.TryLoadExport<object>("Weapons/Materials/M_BaseWeapon.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryLoadExport_PaksFolderDoesNotExist_ReturnsNullInsteadOfThrowing()
    {
        var contentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        var provider = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings { IcarusContentPath = contentPath }));

        // Deliberately never creates contentPath\Paks — the exact "game install is missing/
        // incomplete" case this provider needs to degrade gracefully from.
        var result = provider.TryLoadExport<object>("Weapons/Materials/M_BaseWeapon.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryLoadExportAndProject_IcarusContentPathNotSet_ReturnsNullInsteadOfThrowing()
    {
        var provider = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings()));

        var result = provider.TryLoadExportAndProject<object, object>(
            "Weapons/Materials/M_BaseWeapon.uasset", static export => export);

        Assert.Null(result);
    }

    [Fact]
    public void TryLoadExportAndProject_PaksFolderDoesNotExist_NeverInvokesProject()
    {
        var contentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        var provider = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings { IcarusContentPath = contentPath }));
        var projectCallCount = 0;

        var result = provider.TryLoadExportAndProject<object, object>(
            "Weapons/Materials/M_BaseWeapon.uasset", export => { projectCallCount++; return export; });

        Assert.Null(result);
        Assert.Equal(0, projectCallCount);
    }

    [Fact]
    public void TryLoadExport_CalledRepeatedly_KeepsReturningNullWithoutRetryingAFailedMount()
    {
        // Not directly observable from outside (the cached failed-mount Task is private), but
        // this is the same "settle once, mounted or not" contract CueBaseGameContentProvider's own
        // doc comment promises — repeated calls against a permanently-missing install should keep
        // behaving the same way, not throw on some later call once a retry kicks in.
        var provider = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings()));

        var first = provider.TryLoadExport<object>("Weapons/Materials/M_BaseWeapon.uasset");
        var second = provider.TryLoadExport<object>("Weapons/Materials/M_BaseWeapon.uasset");

        Assert.Null(first);
        Assert.Null(second);
    }

    private sealed class FakeSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;

        public bool Save() => true;
    }
}
