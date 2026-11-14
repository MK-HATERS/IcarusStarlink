using System.IO;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// The Map page: names that would sit on each other, the boxes ticked surviving a restart, Map only,
/// identical mission spots, and Reload / check timing with Update everything.
/// </summary>
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
        Directory.CreateDirectory(Path.Combine(_root, "Paks"));
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
        Directory.CreateDirectory(Path.Combine(_root, "Content", "Paks"));
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

    /// <summary>
    /// Regression (typing in the search lagged): a search that finds the same places as the last one
    /// leaves the markers as they are instead of drawing them all again; identical spots are both found.
    /// </summary>
    [Fact]
    public void ASearchThatFindsTheSamePlaces_KeepsTheMarkers()
    {
        var deep = new WorldFeature(WorldFeatureKind.Cave, "Cave", new(0.2, 0.2)) { WormsMin = 6, WormsMax = 10 };
        var map = new WorldMapInfo("Terrain_016", "Olympus", false, [], [], []);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [map],
            new FeaturesRenderer(new WorldFeatures([deep, deep with { }, new WorldFeature(WorldFeatureKind.OilGeyser, "Oil geyser", new(0.5, 0.5))], [])));
        store.Reload(everything: false);
        var vm = new WorldMapViewModel(store, new Settings(), Path.Combine(_root, "Data")) { SelectedMap = new WorldMapItemViewModel(map) };
        vm.ShowCaves = false;

        vm.SearchText = "tier 3";
        var first = vm.Markers.ToList();
        vm.SearchText = "tier 3 ";

        Assert.Equal(2, first.Count);
        Assert.All(first, m => Assert.True(m.IsFound));
        Assert.Equal(first, vm.Markers);
        Assert.Contains("2 places", vm.SearchResult);
    }

    /// <summary>
    /// Records what it draws; per-world features and one zone image for every layer; its stamp can be
    /// changed (a game update) and its drawing broken (a locked pak).
    /// </summary>
    private sealed class MapRenderer : IWorldMapRenderer
    {
        public Dictionary<string, WorldFeatures> Features { get; } = [];

        public WorldLayerPixels? Layer { get; init; }

        public string StampNow { get; set; } = "1";

        public bool Broken { get; set; }

        /// <summary>A world with picture tiles gets a (tiny, not really a JPEG) picture.</summary>
        public bool WithPicture { get; init; }

        /// <summary>Each drawing waits for this (set by the test) after saying it started.</summary>
        public ManualResetEventSlim? Hold { get; init; }

        public ManualResetEventSlim Drawing { get; } = new(false);

        public List<string> Drawn { get; } = [];

        public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map)
        {
            Drawn.Add(map.Id);
            Drawing.Set();
            Hold?.Wait(TimeSpan.FromSeconds(10));
            if (Broken)
            {
                throw new IOException("The pak is locked.");
            }

            return WithPicture && map.PictureTiles.Count > 0 ? ([1, 2, 3], WorldPictureSource.Picture) : (null, WorldPictureSource.None);
        }

        public WorldLayerPixels? ReadLayer(string heatmapAssetPath) => Layer;

        public string Stamp(WorldMapInfo map) => StampNow;

        public WorldFeatures ReadFeatures(WorldMapInfo map) => Features.GetValueOrDefault(map.Id, WorldFeatures.None);
    }

    /// <summary>Throws (as closing Starlink would stop it) on the given call: Reload reports just before each world it draws.</summary>
    private sealed class StopAt(int call) : IProgress<string>
    {
        private int _calls;

        public void Report(string value)
        {
            if (++_calls == call)
            {
                throw new OperationCanceledException();
            }
        }
    }

    /// <summary>Settings with a game folder that has its paks, and the game data's World folder.</summary>
    private Settings GameReady()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Data", "World"));
        Directory.CreateDirectory(Path.Combine(_root, "Content", "Paks"));
        return new Settings { Current = { IcarusContentPath = Path.Combine(_root, "Content") } };
    }

    private static WorldMapInfo World(string id, string name) => new(id, name, false, [], [], []);

    /// <summary>Regression: a game folder without its paks (moved, or its drive unplugged) made Reload "redraw" every map as nothing.</summary>
    [Fact]
    public async Task AGameFolderWithoutPaks_IsSaid_AndNoMapIsRedrawn()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Data", "World"));
        Directory.CreateDirectory(Path.Combine(_root, "Game"));
        var reads = 0;
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => { reads++; return [World("Terrain_016", "Olympus")]; }, new MapRenderer());
        var vm = new WorldMapViewModel(store, new Settings { Current = { IcarusContentPath = Path.Combine(_root, "Game") } }, Path.Combine(_root, "Data"));

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(0, reads);
        Assert.Contains("Paks", vm.StatusMessage);
        Assert.Contains("Settings", vm.StatusMessage);
    }

    /// <summary>Regression: a first build stopped half-way (Starlink closed) was started over from the first world.</summary>
    [Fact]
    public async Task AnInterruptedFirstBuild_IsFinishedWhenTheMapPageOpens()
    {
        var renderer = new MapRenderer();
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [World("A", "Alpha"), World("B", "Beta"), World("C", "Gamma")], renderer);
        Assert.ThrowsAny<OperationCanceledException>(() => store.Reload(everything: false, new StopAt(3)));
        renderer.Drawn.Clear();
        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data"));

        await vm.LoadAsync();

        Assert.Equal(["C"], renderer.Drawn);
    }

    /// <summary>Regression: Update everything showed the Maps step as Done next to "Couldn't draw Olympus".</summary>
    [Fact]
    public async Task AGameUpdateReloadWhereAMapCantBeDrawn_IsNotReportedAsDone()
    {
        var renderer = new MapRenderer();
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [World("Terrain_016", "Olympus")], renderer);
        store.Reload(everything: false);
        var settings = GameReady();
        settings.Current.RedrawMapsDuringGameUpdate = true;
        var vm = new WorldMapViewModel(store, settings, Path.Combine(_root, "Data"));
        renderer.StampNow = "2";
        renderer.Broken = true;

        var (redrawn, message) = await vm.ReloadForGameUpdateAsync();

        Assert.False(redrawn);
        Assert.Contains("Couldn't draw Olympus", message);
    }

    /// <summary>Regression: after every Reload the picked world was shown twice (and its zoom reset).</summary>
    [Fact]
    public async Task AReload_ShowsThePickedWorldOnce()
    {
        // A fresh read every time, as the real catalog's are: the new item never equals the old one.
        var store = new WorldMapStore(Path.Combine(_root, "World"),
            () => [new WorldMapInfo("Terrain_016", "Olympus", false, [new WorldTile(0, 0, "PROC/x0_y0.uasset")], [], [])], new MapRenderer());
        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data"));
        await vm.ReloadCommand.ExecuteAsync(null);
        var shown = 0;
        vm.Layers.CollectionChanged += (_, e) => shown += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(1, shown);
    }

    private static WorldFeature Mission(double u) => new(WorldFeatureKind.MissionSpot, "Foresty", new(u, 0.5)) { Category = "Objective · Foresty" };

    /// <summary>Regression: picking a world rebuilt its markers more than once (each a thousand or so elements on Olympus).</summary>
    [Fact]
    public void PickingAnotherWorld_RebuildsItsMarkersOnce()
    {
        var renderer = new MapRenderer();
        var (a, b) = (World("A", "Alpha"), World("B", "Beta"));
        renderer.Features["A"] = new WorldFeatures([Mission(0.1)], []);
        renderer.Features["B"] = new WorldFeatures([Mission(0.2), Mission(0.3)], []);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [a, b], renderer);
        store.Reload(everything: false);
        var vm = new WorldMapViewModel(store, new Settings(), Path.Combine(_root, "Data")) { SelectedMap = new WorldMapItemViewModel(a) };
        var rebuilds = 0;
        vm.Markers.CollectionChanged += (_, e) => rebuilds += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        vm.SelectedMap = new WorldMapItemViewModel(b);

        Assert.Equal(1, rebuilds);
    }

    /// <summary>Regression: after switching worlds the search line still counted the previous world's places.</summary>
    [Fact]
    public void SwitchingWorlds_TheSearchLineCountsTheNewWorld()
    {
        var renderer = new MapRenderer();
        var (a, b) = (World("A", "Alpha"), World("B", "Beta"));
        renderer.Features["A"] = new WorldFeatures(
            [new WorldFeature(WorldFeatureKind.OilGeyser, "Oil geyser", new(0.3, 0.3)), new WorldFeature(WorldFeatureKind.OilGeyser, "Oil geyser", new(0.6, 0.6))], []);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [a, b], renderer);
        store.Reload(everything: false);
        var vm = new WorldMapViewModel(store, new Settings(), Path.Combine(_root, "Data")) { SelectedMap = new WorldMapItemViewModel(a) };
        vm.SearchText = "geyser";
        Assert.Contains("2 places", vm.SearchResult);

        vm.SelectedMap = new WorldMapItemViewModel(b);

        Assert.DoesNotContain("places", vm.SearchResult);
        Assert.StartsWith("Nothing here has", vm.SearchResult);
    }

    /// <summary>Regression: Enter right after typing zoomed to the last finished search (or not at all), not to what was typed.</summary>
    [Fact]
    public void EnterRightAfterTyping_ZoomsToWhatWasTyped()
    {
        var renderer = new MapRenderer();
        var a = World("A", "Alpha");
        renderer.Features["A"] = new WorldFeatures(
            [new WorldFeature(WorldFeatureKind.Cave, "Cave", new(0.2, 0.2)), new WorldFeature(WorldFeatureKind.OilGeyser, "Oil geyser", new(0.7, 0.6))], []);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [a], renderer);
        store.Reload(everything: false);
        var vm = new WorldMapViewModel(store, new Settings(), Path.Combine(_root, "Data")) { SelectedMap = new WorldMapItemViewModel(a) };
        vm.SearchText = "cave";
        // From here typing waits for its pause, which never comes on this thread: only Enter can run it.
        vm.UiDispatcher = () => System.Windows.Threading.Dispatcher.CurrentDispatcher;
        vm.SearchText = "geyser";
        var zooms = new List<WorldMapPoint>();
        vm.ZoomRequested += (at, _) => zooms.Add(at);

        vm.ZoomToSearch();

        Assert.Equal([new WorldMapPoint(0.7, 0.6)], zooms);
    }

    /// <summary>Regression: every zone border the mouse crossed made a new whole-map highlight bitmap (two full-map allocations each).</summary>
    [Fact]
    public void HoveringFromZoneToZone_ReusesOneHighlightImage()
    {
        var renderer = new MapRenderer { Layer = new WorldLayerPixels(2, [0xAA0000, 0x00BB00, 0xAA0000, 0x00BB00]) };
        var map = new WorldMapInfo("A", "Alpha", false, [], [],
            [new WorldLayer(WorldLayerKind.Creatures, "Heatmaps/x.uasset",
                new Dictionary<uint, WorldZone> { [0xAA0000] = new("OLY_A", "A", []), [0x00BB00] = new("OLY_B", "B", []) })]);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [map], renderer);
        store.Reload(everything: false);
        var vm = new WorldMapViewModel(store, new Settings(), Path.Combine(_root, "Data")) { SelectedMap = new WorldMapItemViewModel(map) };

        vm.HoverAt(0.25, 0.25);
        var first = vm.HighlightImage;
        vm.HoverAt(0.75, 0.25);

        Assert.NotNull(first);
        Assert.Same(first, vm.HighlightImage);
        var lit = new uint[4];
        ((System.Windows.Media.Imaging.BitmapSource)vm.HighlightImage!).CopyPixels(lit, 8, 0);
        Assert.Equal(0u, lit[0]);
        Assert.NotEqual(0u, lit[1]);
    }

    /// <summary>Regression: a world whose picture file was gone said the game has no picture for it, instead of saying to redraw it.</summary>
    [Fact]
    public async Task AMapWhosePictureFileIsMissing_SaysToReload_NotThatTheGameHasNone()
    {
        var map = new WorldMapInfo("Terrain_016", "Olympus", false, [new WorldTile(0, 0, "PROC/x0_y0.uasset")], [], []);
        var renderer = new MapRenderer { WithPicture = true };
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [map], renderer);
        store.Reload(everything: false);
        File.Delete(store.PicturePath(map.Id));
        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data"));
        renderer.Drawn.Clear();

        await vm.LoadAsync();

        Assert.Empty(renderer.Drawn);
        Assert.Equal(map.Id, vm.SelectedMap?.Map.Id);
        Assert.DoesNotContain("The game has no map picture", vm.MapNote);
        Assert.Contains("Reload", vm.MapNote);
        Assert.Contains("missing", vm.MapEmptyText);
    }

    /// <summary>Regression: with no game folder set, the map area said the game has no picture for the world.</summary>
    [Fact]
    public void WithNoGameFolder_TheMapAreaSaysToSetIt()
    {
        var vm = Build(new Settings());

        Assert.Contains("Settings", vm.MapEmptyText);
    }

    /// <summary>A world with one Creatures zone image (two zones), drawn into a fresh store.</summary>
    private (WorldMapStore Store, WorldMapInfo Map) LayeredWorld()
    {
        var map = new WorldMapInfo("A", "Alpha", false, [], [],
            [new WorldLayer(WorldLayerKind.Creatures, "Heatmaps/x.uasset",
                new Dictionary<uint, WorldZone> { [0xAA0000] = new("OLY_A", "A", []), [0x00BB00] = new("OLY_B", "B", []) })]);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [map],
            new MapRenderer { Layer = new WorldLayerPixels(2, [0xAA0000, 0x00BB00, 0xAA0000, 0x00BB00]) });
        store.Reload(everything: false);
        return (store, map);
    }

    /// <summary>Regression: the picked world's picture, zone images and markers (100+ MB on Olympus) stayed in memory for the session after one visit to Map.</summary>
    [Fact]
    public void ALeftMapPage_LetsGoOfItsImagesWhenIdle_AndShowsThemAgainWhenOpened()
    {
        var (store, map) = LayeredWorld();
        var vm = new WorldMapViewModel(store, new Settings(), Path.Combine(_root, "Data")) { UiDispatcher = () => null };
        vm.SelectedMap = new WorldMapItemViewModel(map);
        Assert.NotEmpty(vm.Layers);
        Assert.NotNull(vm.ZonesImage);

        vm.OnPageLeft();
        Assert.True(vm.ReleaseIfIdle(TimeSpan.Zero));

        Assert.Empty(vm.Layers);
        Assert.Null(vm.ZonesImage);

        vm.OnPageShown();

        Assert.NotEmpty(vm.Layers);
        Assert.NotNull(vm.ZonesImage);
        Assert.False(vm.ReleaseIfIdle(TimeSpan.Zero));
    }

    /// <summary>Regression: Update everything's redraw loaded the first world's picture and zones even when Map was never opened.</summary>
    [Fact]
    public async Task AnUpdateEverythingRedraw_DoesntLoadAWorldWhenThePageWasNeverOpened()
    {
        var (store, _) = LayeredWorld();
        var settings = GameReady();
        settings.Current.RedrawMapsDuringGameUpdate = true;
        var vm = new WorldMapViewModel(store, settings, Path.Combine(_root, "Data"));

        await vm.ReloadForGameUpdateAsync();

        Assert.Single(vm.Maps);
        Assert.Empty(vm.Layers);

        vm.OnPageShown();
        await vm.CheckAsync(); // the check the page opening started

        Assert.NotEmpty(vm.Layers);
    }

    /// <summary>Regression: every visit to Map read the game's tables again for the check, and opening it twice quickly read them twice at once.</summary>
    [Fact]
    public async Task OpeningMapTwiceQuickly_ChecksOnce()
    {
        var reads = 0;
        var renderer = new HeldCheckRenderer();
        var store = new WorldMapStore(Path.Combine(_root, "World"),
            () => { Interlocked.Increment(ref reads); return [World("Terrain_016", "Olympus")]; }, renderer);
        store.Reload(everything: false);
        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data"));
        await vm.ReloadCommand.ExecuteAsync(null);
        reads = 0;
        renderer.StampNow = "2";
        renderer.HoldFirst = true;

        var first = vm.CheckAsync();
        Assert.True(renderer.Holding.Wait(TimeSpan.FromSeconds(10)), "the check never asked for a stamp");
        var second = vm.CheckAsync();
        renderer.Release.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(1, reads);
        Assert.NotNull(vm.OutOfDateMessage);
    }

    /// <summary>Stop while the maps are drawn: the one being drawn is finished and kept, the rest wait, and opening Map carries on.</summary>
    [Fact]
    public async Task Stop_FinishesTheMapItsOn_KeepsWhatsDrawn_AndOpeningMapDrawsTheRest()
    {
        var renderer = new MapRenderer { Hold = new(false) };
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [World("A", "Alpha"), World("B", "Beta"), World("C", "Gamma")], renderer);
        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data"));
        Assert.False(vm.StopCommand.CanExecute(null));

        var reload = vm.ReloadCommand.ExecuteAsync(null);
        Assert.True(renderer.Drawing.Wait(TimeSpan.FromSeconds(10)), "nothing was drawn");
        Assert.True(vm.StopCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        renderer.Hold!.Set();
        await reload;

        Assert.Equal(["A"], renderer.Drawn);
        Assert.Contains("Stopped", vm.StatusMessage);
        Assert.False(vm.StopCommand.CanExecute(null));

        renderer.Drawn.Clear();
        await vm.LoadAsync();

        Assert.Equal(["B", "C"], renderer.Drawn);
    }

    private sealed class ActivityLog : IcarusStarlink.Core.Activity.IActivityLog
    {
        public System.Collections.ObjectModel.ObservableCollection<IcarusStarlink.Core.Activity.ActivityEntry> Entries { get; } = [];

        public void Log(string message, IcarusStarlink.Core.Activity.ActivityEntryKind kind = IcarusStarlink.Core.Activity.ActivityEntryKind.Info) =>
            Entries.Add(new(message, kind, DateTimeOffset.Now));
    }

    /// <summary>Drawing that ends while Map isn't on screen is said in the activity panel, with the maps that couldn't be drawn.</summary>
    [Fact]
    public async Task DrawingThatEndsWhileMapIsntShown_IsSaidInTheActivityPanel()
    {
        var renderer = new MapRenderer();
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [World("Terrain_016", "Olympus")], renderer);
        store.Reload(everything: false);
        var log = new ActivityLog();
        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data"), log);
        renderer.StampNow = "2";
        renderer.Broken = true;

        await vm.ReloadCommand.ExecuteAsync(null);

        var entry = Assert.Single(log.Entries);
        Assert.Contains("Couldn't draw Olympus", entry.Message);
        Assert.Equal(IcarusStarlink.Core.Activity.ActivityEntryKind.Warning, entry.Kind);
    }

    /// <summary>A world with a creature zone on the left half of its (2 x 2) zone image.</summary>
    private static WorldMapInfo Zoned(string id, string name, params WorldZoneEntry[] creatures) =>
        new(id, name, false, [], [],
            [new WorldLayer(WorldLayerKind.Creatures, $"Heatmaps/{id}/GT.uasset",
                new Dictionary<uint, WorldZone> { [0x00FF90] = new("OLY_Conifer_Hard", "Conifer Hard", creatures) })]);

    /// <summary>
    /// Olympus and Styx drawn, then a game update: Olympus's zone gains a Desert Wolf, a cave is new
    /// and a geyser is gone; Styx's files changed too, with nothing to show for it.
    /// </summary>
    private async Task<(WorldMapViewModel Vm, WorldFeature NewCave)> AfterAnUpdateThatChangedOlympusAsync()
    {
        var wolf = new WorldZoneEntry("Conifer_Wolf", "Wolf", 8);
        var updated = false;
        var renderer = new MapRenderer { Layer = new WorldLayerPixels(2, [0x00FF90, 0, 0x00FF90, 0]) };
        var store = new WorldMapStore(Path.Combine(_root, "World"),
            () => [updated ? Zoned("Terrain_016", "Olympus", wolf, new("Desert_Wolf", "Desert Wolf", 4)) : Zoned("Terrain_016", "Olympus", wolf), Zoned("Terrain_017", "Styx", wolf)],
            renderer, () => new DateTime(2030, 5, 17, 12, 0, 0, DateTimeKind.Utc));
        var cave = new WorldFeature(WorldFeatureKind.Cave, "Cave", new(0.25, 0.5)) { WormsMin = 2, WormsMax = 5 };
        var newCave = new WorldFeature(WorldFeatureKind.Cave, "Cave", new(0.75, 0.25)) { WormsMin = 6, WormsMax = 9 };
        renderer.Features["Terrain_016"] = new([cave, new WorldFeature(WorldFeatureKind.OilGeyser, "Oil geyser", new(0.5, 0.75))], []);
        store.Reload(everything: false);
        updated = true;
        renderer.StampNow = "2";
        renderer.Features["Terrain_016"] = new([cave, newCave], []);
        store.Reload(everything: false);

        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data")) { UiDispatcher = () => null };
        await vm.LoadAsync();
        return (vm, newCave);
    }

    [Fact]
    public async Task AWorldThatChanged_IsMarkedInTheMapList_AndOffersWhatChanged()
    {
        var (vm, _) = await AfterAnUpdateThatChangedOlympusAsync();

        Assert.Equal([true, true], vm.Maps.Select(m => m.HasChanges));
        Assert.StartsWith("Changed on ", vm.Maps[0].ChangedText);
        Assert.EndsWith(", changed", vm.Maps[0].ToString());
        Assert.Equal("Olympus", vm.SelectedMap!.Name);
        Assert.True(vm.HasChangeRecord);
        Assert.StartsWith("What changed on ", vm.ChangeTitle);
        Assert.Contains("1 zone changed, 1 new spot, 1 spot gone", vm.ChangesToolTip);

        // Off until it's switched on: nothing extra is drawn on the map or listed.
        Assert.False(vm.ShowChangeList);
        Assert.Empty(vm.ChangeMarkers);
        Assert.Null(vm.ChangesImage);

        // A world whose files changed with nothing to see has the switch too, and says so.
        vm.SelectedMap = vm.Maps[1];

        Assert.True(vm.HasChangeRecord);
        Assert.Equal(["The game changed this map's files, but nothing that shows here: its picture, zones and spots are as they were."], vm.ChangeLines.Select(l => l.Text));
        Assert.Contains("the game's files changed, with nothing to see on the map", vm.ChangesToolTip);
    }

    /// <summary>Olympus (a 4 x 4 in-game grid) drawn, with the given record put in the index as a Reload would have.</summary>
    private async Task<WorldMapViewModel> WithRecordAsync(WorldMapChangeRecord record, bool grid = true)
    {
        var map = new WorldMapInfo("Terrain_016", "Olympus", true, [], [], [],
            Extent: grid ? new WorldExtent(0, 0, 4 * WorldMapInfo.GridSquare, 4 * WorldMapInfo.GridSquare) : null);
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [map], new MapRenderer());
        store.Reload(everything: false);
        var path = Path.Combine(_root, "World", "index.json");
        var index = System.Text.Json.JsonSerializer.Deserialize<WorldMapIndex>(File.ReadAllText(path))!;
        index.Maps["Terrain_016"] = index.Maps["Terrain_016"] with { Changed = record };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(index));
        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data")) { UiDispatcher = () => null };
        await vm.LoadAsync();
        return vm;
    }

    private static readonly DateTime Recorded = new(2030, 5, 17, 12, 0, 0, DateTimeKind.Utc);

    private static uint[] Pixels(System.Windows.Media.ImageSource? source, int count)
    {
        var image = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(source);
        var pixels = new uint[count];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    [Fact]
    public async Task WhatChanged_NamesTheSquaresWhereThePictureAndZoneShapesChanged_AndTheFiles()
    {
        var vm = await WithRecordAsync(new WorldMapChangeRecord
        {
            When = Recorded,
            FilesChanged = true,
            // E4 is past a 4 x 4 grid: D2 (row 1, column 3), then A3 and B3.
            Picture = new WorldImageChange { What = "Picture", Columns = 4, Rows = 4, Cells = [1 * 4 + 3, 2 * 4 + 0, 2 * 4 + 1] },
            Shapes = [new WorldImageChange { What = "Fishing", Columns = 4, Rows = 4, Cells = [0] }, new WorldImageChange { What = "Biomes", Whole = true }],
            Files =
            [
                new WorldFileChange { Kind = "Picture tiles", Changed = ["T_x0_y1", "T_x3_y2"], ChangedCount = 2 },
                new WorldFileChange { Kind = "Level files", Changed = ["Terrain_016"], ChangedCount = 1, Added = ["T016_Volcano_Lair"], AddedCount = 1, Gone = ["T016_Old"], GoneCount = 1 },
            ],
        });

        vm.ShowChanges = true;

        Assert.Equal(
        [
            "Picture changed in A3, B3, D2.",
            "Fishing zones: their shapes changed in A1.",
            "Biomes: their shapes changed all over the map.",
            "Picture tiles changed: T_x0_y1, T_x3_y2.",
            "Level files changed: Terrain_016.",
            "New level files: T016_Volcano_Lair.",
            "Level files gone: T016_Old.",
        ], vm.ChangeLines.Select(l => l.Text));
        Assert.StartsWith("Found by the Reload on ", vm.ChangeWhenText);
        Assert.EndsWith("Kept until this map changes again.", vm.ChangeWhenText);
        Assert.Contains("picture changed, zone shapes changed, 5 game files changed", vm.ChangesToolTip);

        // The picture's squares in pink, the zone shape's in yellow: one pixel per grid square.
        var pink = Pixels(vm.PictureChangesImage, 16);
        Assert.Equal([7, 8, 9], Enumerable.Range(0, 16).Where(i => pink[i] == WorldMapViewModel.PictureChangeColor));
        Assert.Equal(3, pink.Count(p => p != 0));
        var yellow = Pixels(vm.ShapeChangesImage, 16);
        Assert.Equal([0], Enumerable.Range(0, 16).Where(i => yellow[i] == WorldMapViewModel.ShapeChangeColor));
        Assert.NotEqual(WorldMapViewModel.ChangedZoneColor, WorldMapViewModel.PictureChangeColor);

        vm.ShowChanges = false;
        Assert.Null(vm.PictureChangesImage);
        Assert.Null(vm.ShapeChangesImage);
    }

    /// <summary>A whole map that changed is said once, and many squares are counted, not listed one by one.</summary>
    [Fact]
    public async Task WhatChanged_StaysShort_WhenAWholeMapChanged()
    {
        var vm = await WithRecordAsync(new WorldMapChangeRecord
        {
            When = Recorded,
            FilesChanged = true,
            Picture = new WorldImageChange { What = "Picture", Whole = true },
            Shapes = [new WorldImageChange { What = "Creatures", Columns = 4, Rows = 4, Cells = [.. Enumerable.Range(0, 13)] }],
            Files = [new WorldFileChange { Kind = "Level files", Changed = [.. Enumerable.Range(0, 40).Select(i => $"L{i:00}")], ChangedCount = 64 }],
        });

        vm.ShowChanges = true;

        Assert.Equal(
        [
            "The whole picture changed.",
            "Creature zones: their shapes changed in 13 of 16 grid squares (tinted yellow on the map).",
            "Level files changed: L00, L01, L02, L03, L04, L05, L06, L07, L08, L09, L10, L11 and 52 more.",
        ], vm.ChangeLines.Select(l => l.Text));
        Assert.Null(vm.PictureChangesImage);
        Assert.NotNull(vm.ShapeChangesImage);
    }

    [Fact]
    public async Task AWorldWithoutTheInGameGrid_CountsTheChangedParts()
    {
        var vm = await WithRecordAsync(
            new WorldMapChangeRecord { When = Recorded, FilesChanged = true, Picture = new WorldImageChange { What = "Picture", Columns = 8, Rows = 8, Cells = [0, 9] } },
            grid: false);

        Assert.Equal(["Picture changed in 2 of 64 parts of the map (tinted pink)."], vm.ChangeLines.Select(l => l.Text));
    }

    [Fact]
    public async Task AWorldTheGameAdded_IsTaggedNew_NotChanged()
    {
        var vm = await WithRecordAsync(new WorldMapChangeRecord { When = Recorded, NewMap = true });

        var item = Assert.Single(vm.Maps);
        Assert.True(item.IsNew);
        Assert.False(item.HasChanges);
        Assert.StartsWith("New: the game added this map.", item.ChangedText);
        Assert.EndsWith(", in development, new", item.ToString());
        Assert.True(vm.HasChangeRecord);
        Assert.StartsWith("New map, first seen on ", vm.ChangeTitle);
        Assert.Equal(["New map: the game added this world. What changes on it from now on is listed here."], vm.ChangeLines.Select(l => l.Text));
    }

    [Fact]
    public async Task WhatChanged_LightsUpTheChangedZones_MarksNewAndGoneSpots_AndListsThemInWords()
    {
        var (vm, newCave) = await AfterAnUpdateThatChangedOlympusAsync();

        vm.ShowChanges = true;

        Assert.True(vm.ShowChangeList);
        Assert.Equal(
            ["Creature zone Conifer Hard: Desert Wolf added.", "New: Tier 3 cave · 6-9 cave worms", "Gone: Oil geyser"],
            vm.ChangeLines.Select(l => l.Text));
        Assert.Equal([false, true, true], vm.ChangeLines.Select(l => l.CanZoom));
        Assert.Equal([(750d, 250d, "+"), (500d, 750d, "−")], vm.ChangeMarkers.Select(m => (m.X, m.Y, m.Glyph)));
        Assert.Equal("New: Tier 3 cave · 6-9 cave worms", vm.ChangeMarkers[0].ToolTip);

        // The changed zone (the left half of the zone image) is lit; the rest stays clear.
        var image = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(vm.ChangesImage);
        var pixels = new uint[4];
        image.CopyPixels(pixels, 8, 0);
        Assert.Equal([WorldMapViewModel.ChangedZoneColor, 0u, WorldMapViewModel.ChangedZoneColor, 0u], pixels);

        // A spot's line zooms the map to it.
        WorldMapPoint? zoomedTo = null;
        vm.ZoomRequested += (at, _) => zoomedTo = at;
        vm.ZoomToChangeCommand.Execute(vm.ChangeLines[1]);
        Assert.Equal(newCave.At, zoomedTo);

        // Another world has nothing to draw on the map; back on the changed one the switch is still on.
        vm.SelectedMap = vm.Maps[1];
        Assert.Empty(vm.ChangeMarkers);
        Assert.Null(vm.ChangesImage);

        vm.SelectedMap = vm.Maps[0];
        Assert.Equal(2, vm.ChangeMarkers.Count);

        vm.ShowChanges = false;
        Assert.Empty(vm.ChangeMarkers);
        Assert.Null(vm.ChangesImage);
    }

    /// <summary>A reload from the page picks up what it just recorded, without the page being reopened.</summary>
    [Fact]
    public async Task AReloadThatFindsAChange_ShowsTheMarkAndTheSwitchRightAway()
    {
        var renderer = new MapRenderer();
        var store = new WorldMapStore(Path.Combine(_root, "World"), () => [World("Terrain_016", "Olympus")], renderer);
        var cave = new WorldFeature(WorldFeatureKind.Cave, "Cave", new(0.25, 0.5)) { WormsMin = 2, WormsMax = 5 };
        renderer.Features["Terrain_016"] = new([cave], []);
        var vm = new WorldMapViewModel(store, GameReady(), Path.Combine(_root, "Data")) { UiDispatcher = () => null };
        vm.OnPageShown();
        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.False(vm.HasChangeRecord);
        renderer.StampNow = "2";
        renderer.Features["Terrain_016"] = new([cave, cave with { At = new(0.9, 0.9) }], []);

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.True(vm.HasChangeRecord);
        Assert.True(vm.Maps[0].HasChanges);
        Assert.Equal(["New: Tier 2 cave · 2-5 cave worms"], vm.ChangeLines.Select(l => l.Text));
    }

    [Fact]
    public void PuttingTheChoicesBack_SavesNothing()
    {
        var settings = new Settings { Current = { MapShown = ["Grid"] } };

        Build(settings);

        Assert.Equal(0, settings.Saves);
    }
}
