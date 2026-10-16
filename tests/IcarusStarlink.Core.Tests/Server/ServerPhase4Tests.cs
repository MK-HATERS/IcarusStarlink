using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>Phase 4 rules: when automation restarts, updates or leaves a server alone, and which firewall rules let players in.</summary>
public sealed class ServerPhase4Tests
{
    private static readonly DateTime Day = new(2026, 10, 15);

    private static DedicatedServerProfile Profile(Action<DedicatedServerProfile>? configure = null)
    {
        var p = new DedicatedServerProfile { Name = "Friends server", InstallFolder = @"G:\Icarus Server Creation" };
        configure?.Invoke(p);
        return p;
    }

    private static ServerObservation Up(DateTime now, int players = 0) => new(now, Running: true, players, CleanExitLogged: false);

    private static ServerObservation Down(DateTime now, bool clean = false) => new(now, Running: false, 0, clean);

    // ---- Clean exit lines (real lines from a server log) ----

    [Theory]
    [InlineData("[2026.08.10-00.32.24:447][176]LogCore: Engine exit requested (reason: ConsoleCtrl RequestExit)", true)]
    [InlineData("[2026.08.10-00.32.24:697][176]LogExit: Exiting.", true)]
    [InlineData("[2026.08.12-18.05.54:868][  1]IcarusOSSLog: Error: OnResUserTicket : No player found", false)]
    public void IsCleanExit_RecognisesTheServersExitLines(string line, bool expected) =>
        Assert.Equal(expected, ServerLogFilter.IsCleanExit(line));

    // ---- Crash restarts ----

    [Fact]
    public void Crash_IsRestarted_CleanExitAndFirstLookAreNot()
    {
        var p = Profile();
        var a = new ServerAutomation();
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Down(Day)).Kind); // never seen running

        a.Evaluate(p, Up(Day));
        Assert.Equal(AutomationActionKind.RestartAfterCrash, a.Evaluate(p, Down(Day.AddMinutes(1))).Kind);

        a.Evaluate(p, Up(Day.AddMinutes(2)));
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Down(Day.AddMinutes(3), clean: true)).Kind);
    }

    [Fact]
    public void IntendedStop_IsNotACrash_ButAStaleNoteDoesNotHideALaterOne()
    {
        var p = Profile();
        var a = new ServerAutomation();
        a.Evaluate(p, Up(Day));
        a.NoteIntentionalStop(Day);
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Down(Day.AddSeconds(30))).Kind);

        a.Evaluate(p, Up(Day.AddMinutes(1)));
        a.NoteIntentionalStop(Day.AddMinutes(1)); // the stop never happened
        Assert.Equal(AutomationActionKind.RestartAfterCrash, a.Evaluate(p, Down(Day.AddHours(2))).Kind);
    }

    [Fact]
    public void CrashLoop_GivesUpAfterThreeRestartsInTenMinutes_UntilStartedByHand()
    {
        var p = Profile();
        var a = new ServerAutomation();
        var t = Day;
        for (var i = 0; i < ServerAutomation.MaxCrashRestarts; i++)
        {
            a.Evaluate(p, Up(t));
            Assert.Equal(AutomationActionKind.RestartAfterCrash, a.Evaluate(p, Down(t = t.AddMinutes(1))).Kind);
        }

        a.Evaluate(p, Up(t));
        Assert.Equal(AutomationActionKind.GaveUpAfterCrashes, a.Evaluate(p, Down(t = t.AddMinutes(1))).Kind);
        Assert.True(a.GaveUp);
        a.Evaluate(p, Up(t));
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Down(t = t.AddMinutes(1))).Kind);
        Assert.Contains(a.DescribeUpcoming(p, t, 0), l => l.Contains("paused"));

        a.NoteManualStart();
        a.Evaluate(p, Up(t));
        Assert.Equal(AutomationActionKind.RestartAfterCrash, a.Evaluate(p, Down(t.AddMinutes(1))).Kind);
    }

    [Fact]
    public void CrashesSpreadOut_KeepBeingRestarted()
    {
        var p = Profile();
        var a = new ServerAutomation();
        for (var i = 0; i < 6; i++)
        {
            var t = Day.AddMinutes(15 * i);
            a.Evaluate(p, Up(t));
            Assert.Equal(AutomationActionKind.RestartAfterCrash, a.Evaluate(p, Down(t.AddMinutes(1))).Kind);
        }
    }

    [Fact]
    public void Crash_WithCrashRestartOff_IsLeftAlone()
    {
        var p = Profile(x => x.AutoRestartOnCrash = false);
        var a = new ServerAutomation();
        a.Evaluate(p, Up(Day));
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Down(Day.AddMinutes(1))).Kind);
    }

    // ---- Daily restart ----

    private static DedicatedServerProfile Daily(string time) => Profile(x =>
    {
        x.AutoRestartOnCrash = false;
        x.DailyRestartEnabled = true;
        x.DailyRestartTime = time;
    });

    [Fact]
    public void DailyRestart_RunsOnceAtTheTimeWhenEmpty()
    {
        var p = Daily("05:00");
        var a = new ServerAutomation();
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(4).AddMinutes(59))).Kind);
        Assert.Equal(AutomationActionKind.ScheduledRestart, a.Evaluate(p, Up(Day.AddHours(5).AddSeconds(5))).Kind);
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(5).AddMinutes(1))).Kind);
        Assert.Equal(AutomationActionKind.ScheduledRestart, a.Evaluate(p, Up(Day.AddDays(1).AddHours(5).AddSeconds(5))).Kind);
    }

    [Fact]
    public void DailyRestart_WaitsForPlayersUpToAnHour_ThenGoesAhead()
    {
        var p = Daily("05:00");
        var a = new ServerAutomation();
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(5).AddMinutes(1), players: 2)).Kind);
        Assert.Equal(Day.AddHours(6), a.WaitingForEmptyUntil(p, Day.AddHours(5).AddMinutes(1)));
        Assert.Contains(a.DescribeUpcoming(p, Day.AddHours(5).AddMinutes(1), 2), l => l.Contains("waiting for 2 player"));

        var forced = a.Evaluate(p, Up(Day.AddHours(6).AddSeconds(5), players: 2));
        Assert.Equal(AutomationActionKind.ScheduledRestart, forced.Kind);
        Assert.Contains("still online", forced.Reason);
    }

    [Fact]
    public void DailyRestart_IsSkippedWhenTheAppOpensLongAfterIt()
    {
        var p = Daily("05:00");
        var a = new ServerAutomation();
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(9))).Kind);
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(23))).Kind);
    }

    [Fact]
    public void DailyRestart_LateEveningSlot_StillForcedAfterMidnight()
    {
        var p = Daily("23:30");
        var a = new ServerAutomation();
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(23).AddMinutes(31), players: 1)).Kind);
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddDays(1).AddMinutes(10), players: 1)).Kind);
        Assert.Equal(AutomationActionKind.ScheduledRestart, a.Evaluate(p, Up(Day.AddDays(1).AddMinutes(31), players: 1)).Kind);
    }

    [Fact]
    public void DailyRestart_DoesNothingForAStoppedServerOrABadTime()
    {
        var a = new ServerAutomation();
        Assert.Equal(AutomationActionKind.None, a.Evaluate(Daily("05:00"), Down(Day.AddHours(5).AddSeconds(5))).Kind);
        Assert.Equal(AutomationActionKind.None, new ServerAutomation().Evaluate(Daily("5pm"), Up(Day.AddHours(17))).Kind);
        Assert.Contains(new ServerAutomation().DescribeUpcoming(Daily("5pm"), Day, 0), l => l.Contains("isn't a time"));
    }

    [Theory]
    [InlineData("05:00", true)]
    [InlineData("5:30", true)]
    [InlineData(" 23:59 ", true)]
    [InlineData("24:00", false)]
    [InlineData("5", false)]
    [InlineData("", false)]
    public void TryParseDailyTime_Takes24HourTimes(string text, bool ok) =>
        Assert.Equal(ok, ServerAutomation.TryParseDailyTime(text, out _));

    // ---- Auto update ----

    [Fact]
    public void AutoUpdate_ChecksHourly_InstallsOnlyWhenEmpty()
    {
        var p = Profile(x =>
        {
            x.AutoRestartOnCrash = false;
            x.AutoUpdate = true;
        });
        var a = new ServerAutomation();
        Assert.Equal(AutomationActionKind.CheckForUpdate, a.Evaluate(p, Up(Day.AddHours(12))).Kind);
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(12).AddMinutes(30))).Kind);

        a.NoteUpdateCheck(Day.AddHours(12), updateAvailable: true);
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(12).AddMinutes(31), players: 1)).Kind);
        Assert.Equal(AutomationActionKind.UpdateNow, a.Evaluate(p, Up(Day.AddHours(12).AddMinutes(32))).Kind);

        a.NoteUpdateFinished();
        Assert.Equal(AutomationActionKind.None, a.Evaluate(p, Up(Day.AddHours(12).AddMinutes(40))).Kind);
        Assert.Equal(AutomationActionKind.CheckForUpdate, a.Evaluate(p, Up(Day.AddHours(13).AddMinutes(1))).Kind);
    }

    [Fact]
    public void Automation_AllOff_SaysSo()
    {
        var p = Profile(x => x.AutoRestartOnCrash = false);
        Assert.Equal(["No automation is on for this server."], new ServerAutomation().DescribeUpcoming(p, Day, 0));
    }

    // ---- Firewall ----

    private const string Exe = @"G:\Icarus Server Creation\Icarus\Binaries\Win64\IcarusServer-Win64-Shipping.exe";

    [Theory]
    [InlineData("17777", 17777, true)]
    [InlineData("*", 17777, true)]
    [InlineData("Any", 27015, true)]
    [InlineData("", 27015, true)]
    [InlineData("27015-27020", 27016, true)]
    [InlineData("80,443,17777", 17777, true)]
    [InlineData("27016", 27015, false)]
    [InlineData("27016-27020", 27015, false)]
    public void PortMatches_SinglesRangesListsAndAny(string ports, int port, bool expected) =>
        Assert.Equal(expected, ServerFirewall.PortMatches(ports, port));

    [Fact]
    public void ProgramMatches_IgnoresCaseAndTreatsNoProgramAsAll()
    {
        Assert.True(ServerFirewall.ProgramMatches(Exe.ToLowerInvariant(), Exe));
        Assert.True(ServerFirewall.ProgramMatches(null, Exe));
        Assert.True(ServerFirewall.ProgramMatches("*", Exe));
        Assert.False(ServerFirewall.ProgramMatches(@"E:\steamlibrary\steamapps\common\icarus\icarus\binaries\win64\icarus-win64-shipping.exe", Exe));
    }

    /// <summary>Shaped like the rules on the developer's own PC: the client game's rule must not count for the server.</summary>
    [Fact]
    public void Check_CountsOnlyEnabledInboundRulesForThisServer_AndBlockWins()
    {
        List<FirewallRule> rules =
        [
            new("Icarus", true, true, true, ServerFirewall.Udp, "*", @"E:\steamlibrary\steamapps\common\icarus\icarus\binaries\win64\icarus-win64-shipping.exe"),
            new("Icarus Server Query UDP", true, true, true, ServerFirewall.Udp, "27016", Exe),
            new("Disabled", false, true, true, ServerFirewall.Udp, "27015", Exe),
        ];

        Assert.False(ServerFirewall.Check(rules, Exe, "Query port", 27015, ServerFirewall.Udp).Open);
        var open = ServerFirewall.Check(rules, Exe, "Query port", 27016, ServerFirewall.Udp);
        Assert.True(open.Open);
        Assert.Equal("Icarus Server Query UDP", open.RuleName);
        Assert.False(ServerFirewall.Check(rules, Exe, "Game port", 27016, ServerFirewall.Tcp).Open);

        rules.Add(new("icarusserver-win64-shipping.exe", true, true, false, ServerFirewall.AnyProtocol, "*", Exe));
        var blocked = ServerFirewall.Check(rules, Exe, "Query port", 27016, ServerFirewall.Udp);
        Assert.True(blocked.Blocked);
        Assert.False(blocked.Open);
        Assert.Contains("BLOCKED", blocked.Summary);
    }

    [Fact]
    public void NetshCommands_ReplaceStarlinksOwnRules_AndScopeAddsToTheServerExe()
    {
        var p = Profile(x => x.Name = "Friends \"server\" & co");
        var commands = ServerFirewall.BuildNetshCommands(p, Exe, [("Query port", 27015, ServerFirewall.Udp)]);

        Assert.Equal(3, commands.Count(c => c.StartsWith("advfirewall firewall delete rule name=\"Icarus Starlink - Friends server  co (", StringComparison.Ordinal)));
        var add = Assert.Single(commands, c => c.Contains(" add rule "));
        Assert.Contains("dir=in action=allow protocol=UDP localport=27015", add);
        Assert.Contains($"program=\"{Exe}\"", add);
        Assert.DoesNotContain("&", add);

        var script = ServerFirewall.BuildScript(commands);
        Assert.Contains("delete rule", script);
        Assert.Contains(">nul 2>&1", script);
        Assert.Contains("|| exit /b 1", script);
        Assert.EndsWith("exit /b 0\r\n", script);
    }

    [Fact]
    public void RequiredPorts_AreGameAndQueryUdp_PlusOptionalTcp()
    {
        Assert.Equal(2, ServerFirewall.RequiredPorts(17777, 27015, includeGameTcp: false).Count);
        var withTcp = ServerFirewall.RequiredPorts(17777, 27015, includeGameTcp: true);
        Assert.Equal((("Game port", 17777, ServerFirewall.Tcp)), withTcp[2]);
    }
}
