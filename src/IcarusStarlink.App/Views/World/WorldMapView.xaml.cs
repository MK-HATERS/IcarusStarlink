using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.App.Views;

/// <summary>
/// The Map page. The map is a 1000 x 1000 square (picture, then zones, highlight, water, grid,
/// markers and drop sites stacked over it) moved and scaled inside the viewport: scroll zooms around
/// the mouse, a drag moves it, a click without a drag picks the marker or zones there. Where the
/// mouse is goes to the view model as 0-1 across the map.
/// </summary>
public partial class WorldMapView : UserControl
{
    private const double MapSize = 1000;
    private const double MinZoomOfFit = 0.5;
    private const double MaxZoomOfFit = 16;

    private Point? _dragStart;
    private Point _offsetAtDragStart;
    private bool _dragged;
    private double _fitScale = 1;
    private WorldMapViewModel? _viewModel;

    public WorldMapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (_viewModel is not null)
        {
            _viewModel.ZoomRequested -= OnZoomRequested;
        }

        _viewModel = DataContext as WorldMapViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ZoomRequested += OnZoomRequested;
        }
    }

    /// <summary>The view model wants this point (0-1) in the middle, with this much of the map across the view.</summary>
    private void OnZoomRequested(WorldMapPoint at, double span)
    {
        var width = MapViewport.ActualWidth;
        var height = MapViewport.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = Math.Clamp(Math.Min(width, height) / (Math.Clamp(span, 0.01, 1) * MapSize), _fitScale * MinZoomOfFit, _fitScale * MaxZoomOfFit);
        MapScale.ScaleX = MapScale.ScaleY = scale;
        MapOffset.X = width / 2 - at.U * MapSize * scale;
        MapOffset.Y = height / 2 - at.V * MapSize * scale;
        UpdateMarkers();
    }

    /// <summary>Enter in the search box: zoom to what it found.</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel?.ZoomToSearch();
            e.Handled = true;
        }
    }

    /// <summary>Esc: nothing picked any more.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel?.HasAnySelection == true)
        {
            _viewModel.ClearSelection();
            e.Handled = true;
        }
    }

    /// <summary>Another world: show all of it. Drop sites ticked or unticked: their landing spots follow.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorldMapViewModel.SelectedMap))
        {
            Fit();
        }
        else if (e.PropertyName == nameof(WorldMapViewModel.ShowDropSites))
        {
            UpdateMarkers();
        }
    }

    /// <summary>The zoom at which each drop site's landing spots show (times the fit).</summary>
    private const double SpotsFromZoomOfFit = 3;

    /// <summary>Markers and the grid's lines keep their size on screen (scaled by 1 / zoom); landing spots show once zoomed in.</summary>
    private void UpdateMarkers()
    {
        if (Resources["MarkerScale"] is ScaleTransform markerScale && MapScale.ScaleX > 0)
        {
            markerScale.ScaleX = markerScale.ScaleY = 1 / MapScale.ScaleX;
            GridPath.StrokeThickness = 1 / MapScale.ScaleX;
        }

        LandingSpots.Visibility = _viewModel?.ShowDropSites == true && MapScale.ScaleX >= _fitScale * SpotsFromZoomOfFit
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// The map's area changed size (the window, or a status line appearing above it): showing the
    /// whole map, it's fitted again; zoomed in, the same spot stays in the middle (re-fitting here
    /// undid Zoom to it as soon as Copy's message appeared).
    /// </summary>
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wasFitted = Math.Abs(MapScale.ScaleX - _fitScale) < _fitScale * 0.01 || e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0;
        if (wasFitted)
        {
            Fit();
            return;
        }

        _fitScale = Math.Min(e.NewSize.Width, e.NewSize.Height) / MapSize;
        MapOffset.X += (e.NewSize.Width - e.PreviousSize.Width) / 2;
        MapOffset.Y += (e.NewSize.Height - e.PreviousSize.Height) / 2;
        UpdateMarkers();
    }

    private void Fit()
    {
        var width = MapViewport.ActualWidth;
        var height = MapViewport.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _fitScale = Math.Min(width, height) / MapSize;
        MapScale.ScaleX = MapScale.ScaleY = _fitScale;
        MapOffset.X = (width - MapSize * _fitScale) / 2;
        MapOffset.Y = (height - MapSize * _fitScale) / 2;
        UpdateMarkers();
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the point <paramref name="around"/> (in the viewport) where it is.</summary>
    private void Zoom(double factor, Point around)
    {
        var scale = Math.Clamp(MapScale.ScaleX * factor, _fitScale * MinZoomOfFit, _fitScale * MaxZoomOfFit);
        factor = scale / MapScale.ScaleX;
        MapOffset.X = around.X - (around.X - MapOffset.X) * factor;
        MapOffset.Y = around.Y - (around.Y - MapOffset.Y) * factor;
        MapScale.ScaleX = MapScale.ScaleY = scale;
        UpdateMarkers();
    }

    private Point Centre => new(MapViewport.ActualWidth / 2, MapViewport.ActualHeight / 2);

    private void OnZoomIn(object sender, RoutedEventArgs e) => Zoom(1.4, Centre);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Zoom(1 / 1.4, Centre);

    private void OnZoomFit(object sender, RoutedEventArgs e) => Fit();

    private void OnMapWheel(object sender, MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? 1.25 : 1 / 1.25, e.GetPosition(MapViewport));
        e.Handled = true;
    }

    private void OnMapMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks on the buttons over the map (zoom, Map only) are theirs.
        if (e.OriginalSource is DependencyObject source && FindButton(source))
        {
            return;
        }

        _dragStart = e.GetPosition(MapViewport);
        _offsetAtDragStart = new Point(MapOffset.X, MapOffset.Y);
        _dragged = false;
        MapViewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnMapMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        MapViewport.ReleaseMouseCapture();
        _dragStart = null;
        if (_dragged)
        {
            return;
        }

        // A marker (cave, geyser, spot) under the mouse: its facts; anywhere else: the zones there.
        var marker = (MapViewport.InputHitTest(e.GetPosition(MapViewport)) as FrameworkElement)?.DataContext as WorldFeatureMarkerViewModel;
        if (marker is not null)
        {
            _viewModel?.SelectFeature(marker.Feature);
        }
        else if (MapPoint(e) is { } point)
        {
            _viewModel?.SelectAt(point.X, point.Y);
        }
    }

    private void OnMapMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(MapViewport);
        if (_dragStart is { } start)
        {
            var moved = position - start;
            if (Math.Abs(moved.X) + Math.Abs(moved.Y) > 4)
            {
                _dragged = true;
            }

            if (_dragged)
            {
                MapOffset.X = _offsetAtDragStart.X + moved.X;
                MapOffset.Y = _offsetAtDragStart.Y + moved.Y;
                _viewModel?.HoverAt(null, null);
                return;
            }
        }

        var point = MapPoint(e);
        _viewModel?.HoverAt(point?.X, point?.Y);
        HoverTip.Margin = new Thickness(
            Math.Min(position.X + 16, Math.Max(0, MapViewport.ActualWidth - HoverTip.ActualWidth - 4)),
            Math.Min(position.Y + 12, Math.Max(0, MapViewport.ActualHeight - HoverTip.ActualHeight - 4)), 0, 0);
    }

    private void OnMapMouseLeave(object sender, MouseEventArgs e) => _viewModel?.HoverAt(null, null);

    /// <summary>The mouse was taken away mid-drag (Alt+Tab, a dialog): the drag ends, rather than following the mouse until the next click.</summary>
    private void OnMapLostMouseCapture(object sender, MouseEventArgs e) => _dragStart = null;

    /// <summary>Where the mouse is on the map, 0-1 from its left and top; null off the map.</summary>
    private Point? MapPoint(MouseEventArgs e)
    {
        var p = e.GetPosition(MapLayer);
        return p.X is >= 0 and < MapSize && p.Y is >= 0 and < MapSize ? new Point(p.X / MapSize, p.Y / MapSize) : null;
    }

    private static bool FindButton(DependencyObject element)
    {
        for (var current = element; current is not null;
             current = current is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            if (current is Border { Name: "MapViewport" })
            {
                return false;
            }
        }

        return false;
    }
}
