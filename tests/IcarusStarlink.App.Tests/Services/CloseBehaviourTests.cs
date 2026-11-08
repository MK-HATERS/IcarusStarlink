using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Closing the window keeps Starlink in the tray only when the host asked for it and servers rely on it; nothing else is ever held open.</summary>
public sealed class CloseBehaviourTests
{
    [Theory]
    // setting, shutting down, quit asked, servers need Starlink => keep in tray
    [InlineData(true, false, false, true, true)]
    [InlineData(false, false, false, true, false)] // off by default: closes as before
    [InlineData(true, false, false, false, false)] // nothing to watch: closes
    [InlineData(true, true, false, true, false)] // the updater or Windows shutting down
    [InlineData(true, false, true, true, false)] // Quit from the tray
    [InlineData(true, true, true, true, false)]
    public void Decide(bool setting, bool shuttingDown, bool quitAsked, bool serversNeedStarlink, bool keepInTray)
    {
        var choice = CloseBehaviour.Decide(setting, shuttingDown, quitAsked, serversNeedStarlink);

        Assert.Equal(keepInTray ? CloseChoice.KeepInTray : CloseChoice.Close, choice);
    }
}
