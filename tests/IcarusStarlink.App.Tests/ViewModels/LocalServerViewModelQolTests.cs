using System.IO;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Server page polish: world details, the admin list hint, the UE4SS count and Follow.</summary>
public sealed partial class LocalServerViewModelTests
{
    [Fact]
    public void Worlds_ShowDateAndSize_AndMarkTheOneThatLoadsOnStart()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nLoadProspect=Beta\r\nLastProspectName=Alpha\r\n");
        var prospects = DedicatedServerLayout.ProspectsFolder(_install, null);
        Directory.CreateDirectory(prospects);
        File.WriteAllText(Path.Combine(prospects, "Alpha.json"), "{}");
        File.WriteAllText(Path.Combine(prospects, "Beta.json"), "{ }");
        File.SetLastWriteTimeUtc(Path.Combine(prospects, "Alpha.json"), DateTime.UtcNow.AddHours(-2));
        var (vm, _, _, _) = Build(pick: _install);

        vm.AddExistingServerCommand.Execute(null);

        Assert.Equal(["Beta", "Alpha"], vm.ProspectRows.Select(r => r.Name));
        Assert.Equal(["Beta", "Alpha"], vm.Prospects);
        Assert.Equal(3, vm.ProspectRows[0].SizeBytes);
        Assert.True(vm.ProspectRows[0].IsLoadOnStart);
        Assert.False(vm.ProspectRows[1].IsLoadOnStart);
        Assert.True(vm.ProspectRows[1].IsLastPlayed);

        // The mark follows the Settings form as it is typed, before any save.
        vm.LoadProspect = "alpha";
        Assert.False(vm.ProspectRows[0].IsLoadOnStart);
        Assert.True(vm.ProspectRows[1].IsLoadOnStart);
        vm.LoadProspect = "";
        Assert.DoesNotContain(vm.ProspectRows, r => r.IsLoadOnStart);
    }

    [Fact]
    public void AdminListHint_NamesLinesThatArentSteam64Ids()
    {
        var (vm, _, _, _) = Build();

        vm.AdminWhitelistText = "76561198000000000\r\n";
        Assert.Null(vm.AdminListHint);

        vm.AdminWhitelistText = "76561198000000000\r\nJames";
        Assert.Contains("Line 2", vm.AdminListHint);
        Assert.Contains("James", vm.AdminListHint);

        vm.AdminWhitelistText = "a\nb\n76561198000000000";
        Assert.Contains("2 lines", vm.AdminListHint);
    }

    [Fact]
    public void FollowLog_TurnedBackOn_JumpsToTheNewestLine()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var log = DedicatedServerLayout.LogPath(_install, null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "LogNet: Join succeeded: James\r\n");
        vm.Tick();
        var scrolls = 0;
        vm.LogAppended += (_, _) => scrolls++;

        vm.FollowLog = false;
        vm.LogFilter = "join";
        Assert.Equal(0, scrolls);

        vm.FollowLog = true;
        Assert.Equal(1, scrolls);
    }
}
