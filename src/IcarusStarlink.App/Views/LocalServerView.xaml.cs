using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class LocalServerView : UserControl
{
    private LocalServerViewModel? _viewModel;

    public LocalServerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LogAppended -= ScrollLogToEnd;
            _viewModel.SteamCmdOutputAppended -= ScrollSteamCmdToEnd;
        }

        _viewModel = e.NewValue as LocalServerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LogAppended += ScrollLogToEnd;
            _viewModel.SteamCmdOutputAppended += ScrollSteamCmdToEnd;
        }
    }

    private void ScrollSteamCmdToEnd(object? sender, EventArgs e)
    {
        if (SteamCmdOutputList.Items.Count > 0)
        {
            SteamCmdOutputList.ScrollIntoView(SteamCmdOutputList.Items[^1]);
        }
    }

    /// <summary>The keep count is saved as it is typed but applied to existing backups only here, once the number is finished.</summary>
    private void OnBackupsToKeepLostFocus(object sender, RoutedEventArgs e) => _viewModel?.ApplyBackupsToKeep();

    /// <summary>"Follow": keep the newest log line in view as it arrives.</summary>
    private void ScrollLogToEnd(object? sender, EventArgs e)
    {
        if (LogList.Items.Count > 0)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }
}
