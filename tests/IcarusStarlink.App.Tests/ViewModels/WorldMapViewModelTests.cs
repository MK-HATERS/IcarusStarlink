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

    [Fact]
    public void PuttingTheChoicesBack_SavesNothing()
    {
        var settings = new Settings { Current = { MapShown = ["Grid"] } };

        Build(settings);

        Assert.Equal(0, settings.Saves);
    }
}
