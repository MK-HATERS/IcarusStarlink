using System.Windows;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// A DataGridColumn (Visibility, Binding, etc.) isn't part of the visual/logical tree — it lives
/// in DataGrid.Columns, a plain collection, not a place any element sits — so ElementName bindings
/// on a DataGridColumn's own properties don't reliably track updates (confirmed live: toggling a
/// Columns-picker checkbox correctly flipped the bound ShowXColumn property and persisted it, but
/// an ElementName-bound DataGridColumn.Visibility never picked up the change). A Freezable doesn't
/// need to be in the tree to receive inheritance-context updates, so parking one of these in
/// UserControl.Resources with Data="{Binding}" gives every DataGridColumn a working handle back to
/// the page's own DataContext via Source={StaticResource ...} instead of ElementName.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
