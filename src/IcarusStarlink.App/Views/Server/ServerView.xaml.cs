using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ServerView : UserControl
{
    public ServerView()
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
    // the standard workaround for binding a password field.
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerViewModel viewModel)
        {
            viewModel.PasswordInput = PasswordBox.Password;
        }
    }

    // The reverse direction: when the ViewModel itself clears PasswordInput (New/Delete/Save/
    // Connect all do, so a saved password is never left sitting visible after the action that
    // consumed or reset it), clear the actual PasswordBox control too — setting a bound property
    // alone can't reach a PasswordBox, since it has no real binding in the first place.
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerViewModel.PasswordInput)
            && DataContext is ServerViewModel { PasswordInput.Length: 0 }
            && PasswordBox.Password.Length > 0)
        {
            PasswordBox.Clear();
        }
    }
}
