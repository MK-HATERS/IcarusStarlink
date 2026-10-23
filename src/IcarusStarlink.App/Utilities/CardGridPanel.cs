using System.Windows;
using System.Windows.Controls;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Lays cards out in equal-width columns that fill the whole row: the column count follows the
/// width (as many as fit at <see cref="MinColumnWidth"/>), and each row is as tall as its tallest
/// card, so buttons pinned to a card's bottom line up across the row. A WrapPanel of fixed-width
/// cards left a wide empty strip on the right instead.
/// </summary>
public sealed class CardGridPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(CardGridPanel),
        new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(CardGridPanel),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>How many columns fit in <paramref name="width"/>; at least one. Public for tests.</summary>
    public static int ColumnsFor(double width, double minColumnWidth, double spacing)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || minColumnWidth <= 0)
        {
            return 1;
        }

        return Math.Max(1, (int)((width + spacing) / (minColumnWidth + spacing)));
    }

    private double _cellWidth;
    private int _columns = 1;
    private double[] _rowHeights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        _columns = ColumnsFor(width, MinColumnWidth, Spacing);
        _cellWidth = Math.Max(0, (width - Spacing * (_columns - 1)) / _columns);

        var children = InternalChildren;
        var rows = (children.Count + _columns - 1) / _columns;
        _rowHeights = new double[rows];
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(_cellWidth, double.PositiveInfinity));
            var row = i / _columns;
            _rowHeights[row] = Math.Max(_rowHeights[row], child.DesiredSize.Height);
        }

        var height = _rowHeights.Sum() + Spacing * Math.Max(0, rows - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var y = 0.0;
        for (var i = 0; i < children.Count; i++)
        {
            var row = i / _columns;
            var column = i % _columns;
            if (column == 0 && row > 0)
            {
                y += _rowHeights[row - 1] + Spacing;
            }

            var rowHeight = row < _rowHeights.Length ? _rowHeights[row] : 0;
            children[i].Arrange(new Rect(column * (_cellWidth + Spacing), y, _cellWidth, rowHeight));
        }

        return finalSize;
    }
}
