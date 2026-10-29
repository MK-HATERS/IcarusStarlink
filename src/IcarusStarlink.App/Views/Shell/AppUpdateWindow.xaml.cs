using System.ComponentModel;
using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>Shows AppUpdateViewModel's steps; closing it cancels the update (refused once the updater has it).</summary>
public partial class AppUpdateWindow : Window
{
    private readonly AppUpdateViewModel _viewModel;

    public AppUpdateWindow(Window? owner, AppUpdateViewModel viewModel)
    {
        InitializeComponent();
        Owner = owner;
        DataContext = _viewModel = viewModel;
        viewModel.Finished += _ => Dispatcher.Invoke(() =>
        {
            _closing = true;
            Close();
        });
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppUpdateViewModel.CanCancel) or nameof(AppUpdateViewModel.IsStopped))
            {
                CanCloseOrCancel = viewModel.CanCancel || viewModel.IsStopped;
            }
        };
        Loaded += async (_, _) => await viewModel.RunAsync();
    }

    private bool _closing;

    public static readonly DependencyProperty CanCloseOrCancelProperty =
        DependencyProperty.Register(nameof(CanCloseOrCancel), typeof(bool), typeof(AppUpdateWindow), new PropertyMetadata(true));

    /// <summary>False only while the updater takes over (Starlink is about to close).</summary>
    public bool CanCloseOrCancel
    {
        get => (bool)GetValue(CanCloseOrCancelProperty);
        set => SetValue(CanCloseOrCancelProperty, value);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing && !_viewModel.TryClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
