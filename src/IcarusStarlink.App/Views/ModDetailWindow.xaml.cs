using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ModDetailWindow : Window
{
    /// <summary>Reached by the window's own XAML via RelativeSource AncestorType=Window, so the Edit/Delete/Get update buttons can invoke Library's page-level commands even though DataContext here is the mod item itself, not the page.</summary>
    public LibraryViewModel LibraryViewModel { get; }

    private LibraryItemViewModel? _currentItem;
    private Point3D _meshOrbitCenter;
    private double _meshOrbitRadius = 1;
    private double _meshOrbitYaw = 45;
    private double _meshOrbitPitch = 25;
    private Point? _lastMeshMousePosition;

    public ModDetailWindow(LibraryItemViewModel item, LibraryViewModel libraryViewModel)
    {
        LibraryViewModel = libraryViewModel;
        InitializeComponent();
        ShowItem(item);
        Closed += (_, _) =>
        {
            WeakReferenceMessenger.Default.Unregister<LibraryChangedMessage>(this);
            if (_currentItem is not null)
            {
                _currentItem.PropertyChanged -= OnCurrentItemPropertyChanged;
            }
        };
    }

    /// <summary>Shared by the constructor and the Prev/Next buttons — swaps which mod this window
    /// is showing, including re-pointing the "auto-close if this mod stops existing" watch at the
    /// newly-shown mod's own folder rather than the one navigated away from.</summary>
    private void ShowItem(LibraryItemViewModel item)
    {
        // Description/Changes/Files/Readme are all lazy-loaded on first real selection (normally
        // triggered by LibraryViewModel.OnSelectedItemChanged when the main tree's own SelectedItem
        // changes) — Prev/Next never touches that property, it only swaps this window's own
        // DataContext directly, so without this the newly-shown mod's own content just silently
        // never loads. Self-guarding (a one-time flag per item instance), so calling it again for
        // an item already shown once elsewhere is a safe no-op.
        item.EnsureDetailsLoaded();

        if (_currentItem is not null)
        {
            _currentItem.PropertyChanged -= OnCurrentItemPropertyChanged;
        }
        _currentItem = item;
        item.PropertyChanged += OnCurrentItemPropertyChanged;

        DataContext = item;

        // Without this, deleting the mod this window is showing (via its own Delete button, or
        // from the main window's tree while this pop-out is still open) left the window sitting
        // open on stale data — its Edit/Get update buttons would then operate on a folder that no
        // longer exists, surfacing any resulting error on the MAIN window's StatusMessage while
        // this window itself gave no indication anything was wrong.
        WeakReferenceMessenger.Default.Unregister<LibraryChangedMessage>(this);
        var folderName = item.FolderName;
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) =>
        {
            if (!LibraryViewModel.ContainsMod(folderName))
            {
                ((ModDetailWindow)recipient).Close();
            }
        });
    }

    private void OnCurrentItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryItemViewModel.SelectedAssetMesh))
        {
            FrameMeshCamera(_currentItem?.SelectedAssetMesh);
        }
    }

    /// <summary>
    /// A newly-decoded mesh can be any real size/position (Unreal units, typically centimeters) —
    /// reads the Model3D's own computed Bounds to center and scale the orbit camera to it, rather
    /// than assuming a fixed distance that would put a tiny prop off-screen or clip through a
    /// large one. Reset to a fixed starting angle on every new selection so the camera doesn't
    /// carry over a disorienting rotation from whatever the previous mesh was left at.
    /// </summary>
    private void FrameMeshCamera(Model3D? model)
    {
        if (model is null || model.Bounds.IsEmpty)
        {
            return;
        }

        var bounds = model.Bounds;
        _meshOrbitCenter = new Point3D(
            bounds.X + bounds.SizeX / 2,
            bounds.Y + bounds.SizeY / 2,
            bounds.Z + bounds.SizeZ / 2);

        // The bounding SPHERE radius is half the box's diagonal, not the diagonal itself — using
        // the full diagonal as the camera distance (an earlier version of this code did) put the
        // camera roughly half as far away as it needed to be, badly clipping into any mesh whose
        // longest axis pointed anywhere near the viewer (confirmed live against a real elongated
        // mesh — a rifle magazine, 1.15x1.15x5.19 units — which filled the whole frame edge to
        // edge from the default angle). Dividing by tan(halfFOV) is the standard "distance needed
        // to fit a sphere of this radius in frame" formula; the 1.3x margin leaves visible
        // clearance instead of framing exactly to the edge.
        var boundingRadius = Math.Max(0.01, new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2);
        var halfFieldOfView = MeshCamera.FieldOfView / 2 * Math.PI / 180.0;
        _meshOrbitRadius = boundingRadius / Math.Tan(halfFieldOfView) * 1.3;
        _meshOrbitYaw = 45;
        _meshOrbitPitch = 25;
        UpdateMeshCamera();
    }

    /// <summary>Unreal is Z-up, unlike WPF's usual Y-up convention — UpDirection is set to match
    /// rather than transforming any vertex data, so a mesh appears the same way round it would in
    /// the game/editor.</summary>
    private void UpdateMeshCamera()
    {
        var yaw = _meshOrbitYaw * Math.PI / 180.0;
        var pitch = _meshOrbitPitch * Math.PI / 180.0;
        var offset = new Vector3D(
            _meshOrbitRadius * Math.Cos(pitch) * Math.Cos(yaw),
            _meshOrbitRadius * Math.Cos(pitch) * Math.Sin(yaw),
            _meshOrbitRadius * Math.Sin(pitch));
        MeshCamera.Position = _meshOrbitCenter + offset;
        MeshCamera.LookDirection = -offset;
        MeshCamera.UpDirection = new Vector3D(0, 0, 1);
    }

    private void MeshViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _lastMeshMousePosition = e.GetPosition(MeshViewport);
        MeshViewport.CaptureMouse();
    }

    private void MeshViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_lastMeshMousePosition is not { } last || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(MeshViewport);
        var delta = current - last;
        _lastMeshMousePosition = current;
        _meshOrbitYaw -= delta.X * 0.4;
        _meshOrbitPitch = Math.Clamp(_meshOrbitPitch + delta.Y * 0.4, -85, 85);
        UpdateMeshCamera();
    }

    private void MeshViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _lastMeshMousePosition = null;
        MeshViewport.ReleaseMouseCapture();
    }

    private void MeshViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _meshOrbitRadius = Math.Clamp(_meshOrbitRadius * (e.Delta > 0 ? 0.9 : 1.1), 0.01, 1_000_000);
        UpdateMeshCamera();
    }

    private void ShowPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryItemViewModel current
            && LibraryViewModel.GetAdjacentItem(current, -1) is { } previous)
        {
            ShowItem(previous);
        }
    }

    private void ShowNext_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryItemViewModel current
            && LibraryViewModel.GetAdjacentItem(current, 1) is { } next)
        {
            ShowItem(next);
        }
    }
}
