using System.IO;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Map page's names that would sit on each other, and the boxes ticked surviving a restart.</summary>
public sealed class WorldMapViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public int Saves { get; private set; }

        public bool Save()
        {
            Saves++;
            return true;
        }
    }

    private sealed class NoRenderer : IWorldMapRenderer
    {
        public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map) => (null, WorldPictureSource.None);

        public WorldLayerPixels? ReadLayer(string heatmapAssetPath) => null;

        public string Stamp(WorldMapInfo map) => "1";
    }

    private WorldMapViewModel Build(Settings settings) =>
        new(new WorldMapStore(Path.Combine(_root, "World"), () => [], new NoRenderer()), settings, Path.Combine(_root, "Data"));

    [Fact]
    public void NamesCloseTogether_AreMovedDownALine_FarOnesStay()
    {
        var shifts = new double[4];
        WorldMapViewModel.SpreadLabels(
        [
            (new WorldMapPoint(0.78, 0.23), s => shifts[0] = s),
            (new WorldMapPoint(0.86, 0.23), s => shifts[1] = s), // the two big caves at M4: within a label's width
            (new WorldMapPoint(0.80, 0.235), s => shifts[2] = s),
            (new WorldMapPoint(0.20, 0.60), s => shifts[3] = s),
        ]);

        Assert.Equal([0, 16, 32, 0], shifts);
    }

    [Fact]
    public void TheFirstVisit_ShowsCreatureZonesDropSitesAndCaves()
    {
        var vm = Build(new Settings());

        Assert.True(vm.ShowDropSites);
        Assert.True(vm.ShowCaves);
        Assert.False(vm.ShowMissions);
        Assert.Equal("1", vm.CaveTier!.Key);
    }

    [Fact]
    public void WhatsTicked_IsRememberedForNextTime()
    {
        var settings = new Settings();
        var vm = Build(settings);

        vm.ShowGrid = true;
        vm.ShowCaves = false;
        vm.ShowMissions = true;
        vm.CaveTier = vm.CaveTierOptions[2];

        var again = Build(settings);
        Assert.True(again.ShowGrid);
        Assert.False(again.ShowCaves);
        Assert.True(again.ShowMissions);
        Assert.True(again.ShowDropSites);
        Assert.Equal("3", again.CaveTier!.Key);
        Assert.Contains("Creatures", settings.Current.MapShown!);
    }

    /// <summary>Regression (seen in the app): two identical mission spots on one world threw while the map was shown, leaving the page half-drawn.</summary>
    [Fact]
    public void TwoIdenticalMissionSpots_DontStopTheMapShowing()
    {
        var spot = new WorldFeature(WorldFeatureKind.MissionSpot, "Foresty", new(0.45, 0.85)) { Category = "Objective · Foresty" };
        var map = new WorldMapInfo("Terrain_016", "Olympus", false, [], [], []);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [map], new FeaturesRenderer(new WorldFeatures([spot, spot with { }], [])));
        store.Reload(everything: false);
        var vm = new WorldMapViewModel(store, new Settings(), Path.Combine(_root, "Data"));
        vm.ShowMissions = true;

        vm.SelectedMap = new WorldMapItemViewModel(map);

        Assert.Equal(2, vm.Markers.Count);
        Assert.Equal("Mission objective spots: foresty (2)", vm.MissionCategories[1].Title);
    }

    private sealed class FeaturesRenderer(WorldFeatures features) : IWorldMapRenderer
    {
        public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map) => (null, WorldPictureSource.None);

        public WorldLayerPixels? ReadLayer(string heatmapAssetPath) => null;

        public string Stamp(WorldMapInfo map) => "1";

        public WorldFeatures ReadFeatures(WorldMapInfo map) => features;
    }

    [Fact]
    public void MapOnly_HidesTheOverlays_AndClicksPickNothing()
    {
        var vm = Build(new Settings());

        vm.PictureOnly = true;
        vm.SelectAt(0.5, 0.5);
        vm.HoverAt(0.5, 0.5);

        Assert.False(vm.ShowOverlays);
        Assert.False(vm.HasAnySelection);
        Assert.Null(vm.HoverText);
        Assert.True(vm.ShowCaves); // what was ticked stays
    }

    /// <summary>Regression: a reload that threw was reported to Update everything as "redrawn" (Done) with the error as its message.</summary>
    [Fact]
    public async Task AGameUpdateReloadThatFails_IsNotReportedAsRedrawn()
    {
        var map = new WorldMapInfo("Terrain_016", "Olympus", false, [], [], []);
        var tablesBroken = false;
        var store = new WorldMapStore(Path.Combine(_root, "World"),
            () => tablesBroken ? throw new IOException("D_WorldData.json is locked.") : [map], new NoRenderer());
        store.Reload(everything: false);
        Directory.CreateDirectory(Path.Combine(_root, "Data", "World"));
        var settings = new Settings { Current = { IcarusContentPath = _root, RedrawMapsDuringGameUpdate = true } };
        var vm = new WorldMapViewModel(store, settings, Path.Combine(_root, "Data"));
        tablesBroken = true;

        var (redrawn, message) = await vm.ReloadForGameUpdateAsync();

        Assert.False(redrawn);
        Assert.Contains("D_WorldData.json is locked.", message);
    }

    /// <summary>Its first stamp (the check's) waits until the test lets it go; later ones (the reload's) don't.</summary>
    private sealed class HeldCheckRenderer : IWorldMapRenderer
    {
        private int _stamps;

        public string StampNow { get; set; } = "1";

        public bool HoldFirst { get; set; }

        public ManualResetEventSlim Release { get; } = new(false);

        public ManualResetEventSlim Holding { get; } = new(false);

        public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map) => (null, WorldPictureSource.None);

        public WorldLayerPixels? ReadLayer(string heatmapAssetPath) => null;

        public string Stamp(WorldMapInfo map)
        {
            if (HoldFirst && Interlocked.Increment(ref _stamps) == 1)
            {
                Holding.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }

            return StampNow;
        }
    }

    /// <summary>Regression: a check that finished after a Reload had started put the "out of date" bar back up over the redrawn maps.</summary>
    [Fact]
    public async Task ACheckThatFinishesAfterAReloadStarted_DoesntPutTheBarBack()
    {
        var settings = new Settings { Current = { IcarusContentPath = Path.Combine(_root, "Content") } };
        Directory.CreateDirectory(Path.Combine(_root, "Data", "World"));
        var renderer = new HeldCheckRenderer();
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [new WorldMapInfo("Terrain_016", "Olympus", false, [], [], [])], renderer);
        store.Reload(everything: false);
        var vm = new WorldMapViewModel(store, settings, Path.Combine(_root, "Data"));
        renderer.StampNow = "2"; // a game update moved the map's files
        renderer.HoldFirst = true;

        var check = Task.Run(vm.CheckAsync);
        Assert.True(renderer.Holding.Wait(TimeSpan.FromSeconds(10)), "the check never asked for a stamp");
        await vm.ReloadCommand.ExecuteAsync(null);
        renderer.Release.Set();
        await check;

        Assert.Null(vm.OutOfDateMessage);
    }

    [Fact]
    public void PuttingTheChoicesBack_SavesNothing()
    {
        var settings = new Settings { Current = { MapShown = ["Grid"] } };

        Build(settings);

        Assert.Equal(0, settings.Saves);
    }
}
