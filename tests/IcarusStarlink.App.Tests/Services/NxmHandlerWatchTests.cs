using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// Other mod managers take nxm:// links when they start, and players had to keep
/// re-checking Settings to see whether Starlink still had them.
/// </summary>
[Collection(MessengerCollection.Name)]
public class NxmHandlerWatchTests
{
    private const string Starlink = "\"F:\\ModManager\\Icarus Mod Manager\\IcarusStarlink.App.exe\" \"%1\"";
    private const string Other = "\"C:\\Tools\\OtherManager.exe\" \"%1\"";

    private sealed class FakeRegistrar : INxmProtocolRegistrar
    {
        public string? Current { get; set; } = Starlink;
        public int RegisterCalls { get; private set; }

        public bool IsRegisteredToThisApp() => Current == Starlink;
        public string? CurrentHandlerCommand() => Current;
        public string? PreviousHandlerCommand() => null;

        public void Register()
        {
            RegisterCalls++;
            Current = Starlink;
        }

        public void Unregister() => Current = null;
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public bool Save() => true;
    }

    private readonly FakeRegistrar _registrar = new();
    private readonly FakeSettingsService _settings = new();
    private readonly ActivityLog _log = new();

    private DateTimeOffset _time = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private NxmHandlerWatch Watch(bool exesExist = true) => new(_registrar, _settings, _log, () => _time, fileExists: _ => exesExist);

    private const string GoneStarlink = "\"F:\\Gone\\IcarusStarlink.exe\" \"%1\"";

    /// <summary>Links pointed at a Starlink copy that was deleted did nothing, and this Starlink never took them back or said why.</summary>
    [Fact]
    public void ADeletedStarlinkHasTheLinks_TakesThemBackAndSaysItIsGone()
    {
        var watch = Watch(exesExist: false);
        watch.Check();
        _registrar.Current = GoneStarlink;

        watch.Check();

        Assert.Equal(1, _registrar.RegisterCalls);
        Assert.Contains("isn't there any more", _log.Entries[^1].Message);
        Assert.DoesNotContain("passed on", _log.Entries[^1].Message);
    }

    [Fact]
    public void ADeletedStarlinkHasTheLinks_TakeBackOff_WarnsOnce()
    {
        _settings.Current.KeepNxmHandler = false;
        var watch = Watch(exesExist: false);
        watch.Check();
        _registrar.Current = GoneStarlink;

        watch.Check();
        watch.Check();

        Assert.Equal(0, _registrar.RegisterCalls);
        Assert.Equal(1, Warnings);
        Assert.Contains("isn't there any more", _log.Entries.First(e => e.Kind == ActivityEntryKind.Warning).Message);
    }

    /// <summary>Two Starlink installs open at once: the links stay with the other one, without a warning.</summary>
    [Fact]
    public void AnotherStarlinkThatExistsHasTheLinks_LeavesThemAlone()
    {
        var watch = Watch(exesExist: true);
        watch.Check();
        _registrar.Current = GoneStarlink;

        watch.Check();

        Assert.Equal(0, _registrar.RegisterCalls);
        Assert.Equal(0, Warnings);
    }

    private int Warnings => _log.Entries.Count(e => e.Kind == ActivityEntryKind.Warning);

    [Fact]
    public void Registered_RemembersItWasSetUp()
    {
        Watch().Check();

        Assert.True(_settings.Current.NxmHandlerSetUp);
        Assert.Equal(0, Warnings);
    }

    [Fact]
    public void TakenByAnotherApp_WarnsOnceNamingIt_NotOnEveryWindowSwitch()
    {
        _settings.Current.KeepNxmHandler = false;
        var watch = Watch();
        watch.Check();
        _registrar.Current = Other;

        watch.Check();
        watch.Check();

        Assert.Equal(1, Warnings);
        Assert.Contains("OtherManager", _log.Entries[0].Message);
        Assert.Equal(0, _registrar.RegisterCalls);
    }

    [Fact]
    public void TakenAgainAfterTakingBack_WarnsAgain()
    {
        _settings.Current.KeepNxmHandler = false;
        var watch = Watch();
        watch.Check();
        _registrar.Current = Other;
        watch.Check();
        _registrar.Current = Starlink;
        watch.Check();
        _registrar.Current = Other;

        watch.Check();

        Assert.Equal(2, Warnings);
    }

    [Fact]
    public void TakeBackAutomaticallyOn_RegistersAgainWithoutAWarning()
    {
        var watch = Watch();
        watch.Check();
        _registrar.Current = Other;

        watch.Check();

        Assert.Equal(1, _registrar.RegisterCalls);
        Assert.True(_registrar.IsRegisteredToThisApp());
        Assert.Equal(0, Warnings);
    }

    /// <summary>Never set up, or Unregistered on purpose: someone else having the links is not news.</summary>
    [Fact]
    public void NeverSetUp_StaysQuietAndTakesNothing()
    {
        _settings.Current.KeepNxmHandler = true;
        _registrar.Current = Other;

        Watch().Check();

        Assert.Equal(0, _registrar.RegisterCalls);
        Assert.Empty(_log.Entries);
    }

    /// <summary>An app that also takes the links back on every change would trade them with Starlink forever.</summary>
    [Fact]
    public void AnotherAppKeepsTakingThemBack_StarlinkStopsAndWarnsInsteadOfFighting()
    {
        var watch = Watch();
        watch.Check();

        for (var i = 0; i < NxmHandlerWatch.MaxTakeBacks + 2; i++)
        {
            _registrar.Current = Other;
            watch.Check();
            _time += TimeSpan.FromSeconds(5);
        }

        Assert.Equal(NxmHandlerWatch.MaxTakeBacks, _registrar.RegisterCalls);
        Assert.Equal(1, Warnings);
        Assert.Contains("keeps taking", _log.Entries.First(e => e.Kind == ActivityEntryKind.Warning).Message);

        _time += NxmHandlerWatch.FightPause;
        watch.Check();
        Assert.Equal(NxmHandlerWatch.MaxTakeBacks + 1, _registrar.RegisterCalls);
    }

    [Fact]
    public void KeepNxmHandler_IsOnByDefault() => Assert.True(new AppSettings().KeepNxmHandler);

    /// <summary>Settings said "IcarusStarlink.App has them now" (the exe's product name) for a second Starlink install.</summary>
    [Theory]
    [InlineData(@"""F:\ModManager\IcarusStarlink.App.exe"" ""%1""")]
    [InlineData(@"""F:\ModManager\IcarusStarlink.exe"" ""%1""")]
    public void HandlerName_AnotherStarlink_SaysSoWithItsFolder(string command) =>
        Assert.Equal(@"another Starlink install (F:\ModManager)", NxmHandlerWatch.HandlerName(command, ownFolder: @"C:\Apps\Starlink\"));

    [Fact]
    public void HandlerName_ThisInstallsOldExe_NamesIt() =>
        Assert.Equal("this Starlink's old IcarusStarlink.App.exe",
            NxmHandlerWatch.HandlerName(@"""C:\Apps\Starlink\IcarusStarlink.App.exe"" ""%1""", ownFolder: @"C:\Apps\Starlink\"));

    [Fact]
    public void HandlerName_OtherApp_KeepsItsFileName() =>
        Assert.Equal("OtherManager", NxmHandlerWatch.HandlerName(Other, ownFolder: @"C:\Apps\Starlink"));

    [Fact]
    public void ShortFolder_LongPath_KeepsDriveAndLastFolder() =>
        Assert.Equal(@"F:\…\Icarus Mod Manager", NxmHandlerWatch.ShortFolder(@"F:\Games\Modding\Some Very Long Folder Name\Icarus Mod Manager"));
}
