using System.IO;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>"Create prospect on start" as dropdowns: built from D_ProspectList, read back from the ini's text.</summary>
public sealed partial class LocalServerViewModelTests
{
    private LocalServerViewModel BuildWithProspects()
    {
        var data = Path.Combine(_root, "Data");
        Directory.CreateDirectory(Path.Combine(data, "Prospects"));
        File.WriteAllText(Path.Combine(data, "Prospects", "D_ProspectList.json"), """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"OpenWorld_Olympus","DropName":"NSLOCTEXT(\"D_ProspectList\", \"OpenWorld_Olympus-DropName\", \"Olympus\")","bIsOpenWorld":true},
              {"Name":"Outpost006_Olympus","DropName":"NSLOCTEXT(\"D_ProspectList\", \"Outpost006_Olympus-DropName\", \"Outpost: Olympus\")","bIsOpenWorld":false},
              {"Name":"Tier1_Forest_Recon_0","DropName":"NSLOCTEXT(\"D_ProspectList\", \"Tier1_Forest_Recon_0-DropName\", \"Recon\")","bIsOpenWorld":false}
            ]}
            """);
        return new LocalServerViewModel(new FakeStore(), new FakeProcesses(), new FakeDialogs(true), new ActivityLog(), SteamCmd, Settings, MergedPakPath, BackupsRoot, _ => null, () => null, startTimer: false, firewall: Firewall)
        {
            DataFolder = data,
        };
    }

    [Fact]
    public void ProspectChoices_ComeFromTheGameList_OpenWorldsFirst()
    {
        var vm = BuildWithProspects();

        Assert.True(vm.HasProspectChoices);
        Assert.Equal(["", "OpenWorld_Olympus", "Outpost006_Olympus", "Tier1_Forest_Recon_0"], vm.ProspectChoices.Select(c => c.Name));
        Assert.Equal("Olympus", vm.ProspectChoices[1].Label);
        Assert.Equal(["Open world", "Outpost", "Mission"], vm.ProspectChoices.Skip(1).Select(c => c.Kind));
    }

    [Fact]
    public void PickingChoices_WritesTheServersOwnFormat()
    {
        var vm = BuildWithProspects();

        vm.NewProspectType = vm.ProspectChoices.First(c => c.Name == "Outpost006_Olympus");
        vm.NewProspectDifficulty = vm.ProspectDifficulties.First(d => d.Value == 4);
        vm.NewProspectHardcore = true;
        vm.NewProspectName = "My  World";

        Assert.Equal("Outpost006_Olympus 4 True My_World", vm.CreateProspect);

        vm.NewProspectType = vm.ProspectChoices[0];
        Assert.Equal("", vm.CreateProspect);
        Assert.False(vm.IsCreatingProspect);
    }

    [Fact]
    public void TheIniText_FillsTheChoices_EvenForAnUnknownType()
    {
        var vm = BuildWithProspects();

        vm.CreateProspect = "openworld_olympus 3 false Test01";
        Assert.Equal("OpenWorld_Olympus", vm.NewProspectType?.Name);
        Assert.Equal(3, vm.NewProspectDifficulty?.Value);
        Assert.False(vm.NewProspectHardcore);
        Assert.Equal("Test01", vm.NewProspectName);

        vm.CreateProspect = "Tier9_Mystery 2 True";
        Assert.Equal("Tier9_Mystery", vm.NewProspectType?.Name);
        Assert.Equal("not in your game data", vm.NewProspectType?.Kind);
        Assert.Equal("Tier9_Mystery 2 True", vm.CreateProspect);
    }

    [Fact]
    public void WithoutGameData_TheTextBoxStays()
    {
        var (vm, _, _, _) = Build();

        Assert.False(vm.HasProspectChoices);
        Assert.True(vm.HasNoProspectChoices);
    }
}
