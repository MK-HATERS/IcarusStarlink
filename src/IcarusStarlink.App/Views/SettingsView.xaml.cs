using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
            {
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            }

            if (e.NewValue is INotifyPropertyChanged newVm)
            {
                newVm.PropertyChanged += ViewModel_PropertyChanged;
            }
        };
    }

    // PasswordBox.Password is deliberately not a bindable DependencyProperty (a WPF security
    // design, not an oversight) — this pushes it into the ViewModel on every keystroke instead,
    // the standard workaround for binding a password field (matches ServerView's own PasswordBox
    // wiring for the same reason).
    private void GitHubTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.GitHubTokenInput = GitHubTokenBox.Password;
        }
    }

    // The reverse direction: once SaveGitHubTokenCommand clears GitHubTokenInput, clear the actual
    // PasswordBox control too — setting a bound property alone can't reach a PasswordBox, since it
    // has no real binding in the first place.
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.GitHubTokenInput)
            && DataContext is SettingsViewModel { GitHubTokenInput.Length: 0 }
            && GitHubTokenBox.Password.Length > 0)
        {
            GitHubTokenBox.Clear();
        }
    }
}
