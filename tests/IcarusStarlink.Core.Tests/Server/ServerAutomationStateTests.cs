using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>What ServerAutomation tells the Overview: how the last run ended, and what comes next for the options that are on.</summary>
public sealed class ServerAutomationStateTests
{
    private static readonly DateTime Day = new(2026, 10, 15);

    private static DedicatedServerProfile Profile(Action<DedicatedServerProfile>? configure = null)
    {
        var p = new DedicatedServerProfile { Name = "Friends server", InstallFolder = @"C:\Servers\Icarus" };
        configure?.Invoke(p);
        return p;
    }

    private static ServerObservation Up(DateTime now, int pid = 9) => new(now, Running: true, 0, CleanExitLogged: false, pid);

    private static ServerObservation Down(DateTime now, bool clean = false) => new(now, Running: false, 0, clean);

    [Fact]
    public void LastStop_CleanExit_OwnerStop_AndCrash_AreToldApart()
    {
        var p = Profile();
        var a = new ServerAutomation();
        Assert.Null(a.LastStoppedAt);

        a.Evaluate(p, Up(Day));
        a.Evaluate(p, Down(Day.AddMinutes(1), clean: true));
        Assert.Equal((Day.AddMinutes(1), ServerStopKind.Clean), (a.LastStoppedAt!.Value, a.LastStopKind));

        a.Evaluate(p, Up(Day.AddMinutes(2), pid: 10));
        a.NoteOwnerStop(10);
        a.Evaluate(p, Down(Day.AddMinutes(3), clean: true));
        Assert.Equal(ServerStopKind.ByOwner, a.LastStopKind);

        a.Evaluate(p, Up(Day.AddMinutes(4), pid: 11));
        a.Evaluate(p, Down(Day.AddMinutes(5)));
        Assert.Equal((Day.AddMinutes(5), ServerStopKind.Crash), (a.LastStoppedAt!.Value, a.LastStopKind));
    }

    /// <summary>With crash restarts off a crash is still a crash (the Overview says it wasn't restarted).</summary>
    [Fact]
    public void LastStop_CrashWithRestartOff_IsStillACrash()
    {
        var p = Profile(x => x.AutoRestartOnCrash = false);
        var a = new ServerAutomation();
        a.Evaluate(p, Up(Day));
        a.Evaluate(p, Down(Day.AddMinutes(1)));
        Assert.Equal(ServerStopKind.Crash, a.LastStopKind);
    }

    [Fact]
    public void CrashRestartsInWindow_CountsTheLastTenMinutes()
    {
        var p = Profile();
        var a = new ServerAutomation();
        a.Evaluate(p, Up(Day));
        a.Evaluate(p, Down(Day.AddMinutes(1)));
        a.Evaluate(p, Up(Day.AddMinutes(2)));
        a.Evaluate(p, Down(Day.AddMinutes(3)));

        Assert.Equal(2, a.CrashRestartsInWindow(Day.AddMinutes(4)));
        Assert.Equal(1, a.CrashRestartsInWindow(Day.AddMinutes(12)));
        Assert.Equal(0, a.CrashRestartsInWindow(Day.AddMinutes(30)));
        Assert.Contains(a.DescribeUpcoming(p, Day.AddMinutes(4), 0), l => l.Contains("2 of 3 restarts used in the last 10 minutes"));
        Assert.Contains(a.DescribeUpcoming(p, Day.AddMinutes(30), 0), l => l.StartsWith("Restarts after a crash, up to 3 times", StringComparison.Ordinal));
    }

    [Fact]
    public void NextDailyRestart_TodayIfAhead_ElseTomorrow()
    {
        var p = Profile(x => { x.DailyRestartEnabled = true; x.DailyRestartTime = "05:00"; });
        var a = new ServerAutomation();

        Assert.Equal(Day.AddHours(5), a.NextDailyRestart(p, Day.AddHours(4)));
        Assert.Equal(Day.AddDays(1).AddHours(5), a.NextDailyRestart(p, Day.AddHours(7)));
        Assert.Contains(a.DescribeUpcoming(p, Day.AddHours(4), 0), l => l == "Next daily restart today at 05:00.");
        Assert.Contains(a.DescribeUpcoming(p, Day.AddHours(7), 0), l => l == "Next daily restart tomorrow at 05:00.");
        Assert.Null(a.NextDailyRestart(Profile(), Day));
    }

    [Fact]
    public void DailyRestartDueButPlayersOnline_SaysItIsWaiting()
    {
        var p = Profile(x => { x.DailyRestartEnabled = true; x.DailyRestartTime = "05:00"; });
        var a = new ServerAutomation();
        Assert.Contains(a.DescribeUpcoming(p, Day.AddHours(5).AddMinutes(5), 2), l => l.Contains("waiting for 2 player(s) to leave") && l.Contains("06:00"));
    }

    [Fact]
    public void NextUpdateCheck_IsAnHourAfterTheLastOne_OnlyWhileItRuns()
    {
        var p = Profile(x => x.AutoUpdate = true);
        var a = new ServerAutomation();
        Assert.Null(a.NextUpdateCheck);
        Assert.Contains(a.DescribeUpcoming(p, Day, 0), l => l == "Checks for an update as soon as it runs.");

        Assert.Equal(AutomationActionKind.CheckForUpdate, a.Evaluate(p, Up(Day.AddHours(12))).Kind);
        Assert.Equal(Day.AddHours(13), a.NextUpdateCheck);
        Assert.Contains(a.DescribeUpcoming(p, Day.AddHours(12).AddMinutes(1), 0), l => l == "Next update check at 13:00 (only while it runs).");
    }

    [Fact]
    public void NextDailyBackup_IsADayAfterTheLastOne()
    {
        var p = Profile(x => x.DailyBackup = true);
        var a = new ServerAutomation();
        a.NoteDailyBackup(Day.AddHours(3));

        Assert.Equal(Day.AddDays(1).AddHours(3), a.NextDailyBackup);
        Assert.Contains(a.DescribeUpcoming(p, Day.AddHours(4), 0), l => l == "Next daily backup tomorrow around 03:00 (only while it runs).");
        Assert.Contains(a.DescribeUpcoming(p, Day.AddDays(2), 0), l => l == "Backs up its worlds as soon as it runs (daily backup).");
    }

    /// <summary>Only the options that are on get a line.</summary>
    [Fact]
    public void DescribeUpcoming_LeavesOutOptionsThatAreOff()
    {
        var p = Profile(x => { x.AutoRestartOnCrash = false; x.DailyBackup = true; });
        var lines = new ServerAutomation().DescribeUpcoming(p, Day, 0);
        Assert.Equal(["Backs up its worlds as soon as it runs (daily backup)."], lines);
    }
}
