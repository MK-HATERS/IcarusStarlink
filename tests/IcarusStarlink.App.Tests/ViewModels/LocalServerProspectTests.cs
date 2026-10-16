using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>"Create prospect on start" as dropdowns: built from D_ProspectList, read back from the ini's text.</summary>
[Collection(MessengerCollection.Name)]
public sealed partial class LocalServerViewModelTests
{
    private string ProspectDataFolder => Path.Combine(_root, "Data");

    private void WriteProspectList()
    {
        var data = ProspectDataFolder;
        Directory.CreateDirectory(Path.Combine(data, "Prospects"));
        File.WriteAllText(Path.Combine(data, "Prospects", "D_ProspectList.json"), """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"OpenWorld_Olympus","DropName":"NSLOCTEXT(\"D_ProspectList\", \"OpenWorld_Olympus-DropName\", \"Olympus\")","bIsOpenWorld":true},
              {"Name":"Outpost006_Olympus","DropName":"NSLOCTEXT(\"D_ProspectList\", \"Outpost006_Olympus-DropName\", \"Outpost: Olympus\")","bIsOpenWorld":false},
              {"Name":"Tier1_Forest_Recon_0","DropName":"NSLOCTEXT(\"D_ProspectList\", \"Tier1_Forest_Recon_0-DropName\", \"Recon\")","bIsOpenWorld":false}
            ]}
            """);
    }

    private LocalServerViewModel BuildWithProspects()
    {
        WriteProspectList();
        return new LocalServerViewModel(new FakeStore(), new FakeProcesses(), new FakeDialogs(true), new ActivityLog(), SteamCmd, Settings, MergedPakPath, BackupsRoot, _ => null, () => null, startTimer: false, firewall: Firewall, dataFolder: ProspectDataFolder);
    }

    /// <summary>
    /// A real bug: the data folder used to arrive as an init property, AFTER the constructor had
    /// already selected the first profile and cached a choice list read with no data folder — so
    /// the dropdown only ever held "Don't create one".
    /// </summary>
    [Fact]
    public void DataFolderGivenAtConstruction_TheFirstProfileSeesTheGameList()
    {
        WriteProspectList();
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nCreateProspect=OpenWorld_Olympus 2 False World1\r\n");
        var store = new FakeStore();
        store.Profiles.Add(new DedicatedServerProfile { Name = "Friends", InstallFolder = _install });

        var vm = new LocalServerViewModel(store, new FakeProcesses(), new FakeDialogs(true), new ActivityLog(), SteamCmd, Settings, MergedPakPath, BackupsRoot, _ => null, () => null, startTimer: false, firewall: Firewall, dataFolder: ProspectDataFolder);

        Assert.True(vm.HasProspectChoices);
        Assert.Equal(4, vm.ProspectChoices.Count);
        Assert.Equal("Open world", vm.NewProspectType?.Kind);
        Assert.Equal("OpenWorld_Olympus 2 False World1", vm.CreateProspect);
    }

    [Fact]
    public void RefreshGameData_RebuildsTheChoices()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nCreateProspect=OpenWorld_Olympus 2 False\r\n");
        var store = new FakeStore();
        store.Profiles.Add(new DedicatedServerProfile { Name = "Friends", InstallFolder = _install });
        var vm = new LocalServerViewModel(store, new FakeProcesses(), new FakeDialogs(true), new ActivityLog(), SteamCmd, Settings, MergedPakPath, BackupsRoot, _ => null, () => null, startTimer: false, firewall: Firewall, dataFolder: ProspectDataFolder);
        try
        {
            Assert.False(vm.HasProspectChoices);
            Assert.Equal("not in your game data", vm.NewProspectType?.Kind);

            WriteProspectList();
            WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

            Assert.True(vm.HasProspectChoices);
            Assert.Equal("Open world", vm.NewProspectType?.Kind);
            Assert.Equal("OpenWorld_Olympus 2 False", vm.CreateProspect);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    [Fact]
    public void ServerStopping_RereadsTheWorldsAndLastProspect()
    {
        var (vm, _, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(5, DateTime.Now);
        vm.Tick();
        Assert.Empty(vm.Prospects);

        // What the server writes while it runs.
        var prospects = DedicatedServerLayout.ProspectsFolder(_install, null);
        Directory.CreateDirectory(prospects);
        File.WriteAllText(Path.Combine(prospects, "NewWorld.json"), "{}");
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nLastProspectName=NewWorld\r\n");

        processes.Running = null;
        vm.Tick();

        Assert.Equal(["NewWorld"], vm.Prospects);
        Assert.Equal("NewWorld", vm.LastProspectName);
    }

    [Fact]
    public void MergedPakBuiltLater_InstallBecomesAvailable()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        Assert.False(vm.HasMergedPak);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
            File.WriteAllBytes(MergedPakPath, [1, 2, 3]);
            WeakReferenceMessenger.Default.Send(new MergedPakBuiltMessage());

            Assert.True(vm.HasMergedPak);
            Assert.StartsWith("Your merged pak:", vm.MergedPakSummary);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    [Fact]
    public void InstallMergedPak_DoesNotCallAnOldMergedPakLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
        File.WriteAllBytes(MergedPakPath, [1, 2, 3]);
        Directory.CreateDirectory(DedicatedServerLayout.PakModsFolder(_install));
        File.WriteAllBytes(Path.Combine(DedicatedServerLayout.PakModsFolder(_install), "ISL-Merged_9999_P.pak"), [9]);
        File.WriteAllBytes(Path.Combine(DedicatedServerLayout.PakModsFolder(_install), "SomeOther_P.pak"), [9]);
        var (vm, _, _, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        vm.InstallMergedPakCommand.Execute(null);

        var confirm = Assert.Single(dialogs.Messages, m => m.Contains("Copy your merged pak"));
        Assert.Contains("SomeOther_P.pak", confirm);
        Assert.DoesNotContain("ISL-Merged_9999_P.pak", confirm);
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
