using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>Players and events from the server log: lines that only look like events, and names the login line cuts short.</summary>
public sealed partial class ServerFilesTests
{
    /// <summary>
    /// Regression: the join and shutdown checks matched anywhere in a line, so a log line that only
    /// quoted them (a player name or chat) added a ghost player, then emptied the online list.
    /// </summary>
    [Fact]
    public void LogEvents_EventTextInsideAnotherLine_IsIgnored()
    {
        var events = new ServerLogEvents();
        events.Feed(Login);
        events.Feed(Join);

        Assert.DoesNotContain(events.Feed("[2026.08.12-17.33.00:000][  1]LogTemp: Display: Bob says LogNet: Join succeeded: Ghost"),
            e => e.Kind == ServerLogEventKind.PlayerJoined);
        Assert.Single(events.Online);

        Assert.DoesNotContain(events.Feed("[2026.08.12-17.33.05:000][  1]LogTemp: Display: Engine exit requested (reason: ConsoleCtrl RequestExit)"),
            e => e.Kind == ServerLogEventKind.ShutdownRequested);
        Assert.Equal("TestPlayer", Assert.Single(events.Online).Name);
    }

    /// <summary>
    /// Regression: the login line's option parser cuts "Who?" to "Who", so the join couldn't be
    /// paired with its login id and the player never left the online list.
    /// </summary>
    [Fact]
    public void LogEvents_JoinNameWithAQuestionMark_StillLeaves()
    {
        var events = new ServerLogEvents();
        events.Feed("[2026.08.12-17.31.50:975][102]LogNet: Login request: ?password=x?Name=Who? userId: Steam:UNKNOWN [0x2] platform: Steam");
        events.Feed("[2026.08.12-17.31.51:477][117]LogNet: Join succeeded: Who?");
        Assert.Equal("Who?", Assert.Single(events.Online).Name);

        events.Feed("[2026.08.12-17.32.04:482][428]LogNet: UNetConnection::Close: [UNetConnection] RemoteAddr: 76561198000000002:17777, Name: SteamNetConnection_2147481943, Driver: GameNetDriver SteamNetDriver_2147481984, IsServer: YES, PC: BP_IcarusPlayerControllerServer_C_2147481936, Owner: BP_IcarusPlayerControllerServer_C_2147481936, UniqueId: Steam:UNKNOWN [0x2], Channels: 7, Time: 2026.08.12-17.32.04");
        events.ExpireStale(new DateTime(2026, 8, 12, 17, 33, 5, DateTimeKind.Utc));

        Assert.Empty(events.Online);
    }

    /// <summary>Two logins before one join: the join can't be told apart, so it isn't paired with either id.</summary>
    [Fact]
    public void LogEvents_TwoLoginsThenARenamedJoin_IsNotPairedWithEither()
    {
        var events = new ServerLogEvents();
        events.Feed("[2026.08.12-17.31.50:975][102]LogNet: Login request: ?password=x?Name=Who? userId: Steam:UNKNOWN [0x2] platform: Steam");
        events.Feed("[2026.08.12-17.31.50:990][103]LogNet: Login request: ?password=x?Name=Other userId: Steam:UNKNOWN [0x3] platform: Steam");
        events.Feed("[2026.08.12-17.31.51:477][117]LogNet: Join succeeded: Who?");

        Assert.Equal("Who?", Assert.Single(events.Online).PlayerId);
    }
}
