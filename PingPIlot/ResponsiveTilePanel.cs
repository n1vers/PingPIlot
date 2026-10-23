using System.Windows;
using System.Windows.Controls;

namespace PingPilot;

/// <summary>
/// Панель плиток, которая подстраивается под ширину окна: сама выбирает число колонок
/// так, чтобы плитка не становилась уже MinTileWidth, и растягивает плитки на всю ширину.
/// Высота плитки следует за шириной (HeightRatio), но не меньше MinTileHeight.
/// </summary>
public class ResponsiveTilePanel : Panel
{
    public static readonly DependencyProperty MinTileWidthProperty = DependencyProperty.Register(
        nameof(MinTileWidth), typeof(double), typeof(ResponsiveTilePanel),
        new FrameworkPropertyMetadata(250.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxTileWidthProperty = DependencyProperty.Register(
        nameof(MaxTileWidth), typeof(double), typeof(ResponsiveTilePanel),
        new FrameworkPropertyMetadata(460.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinTileHeightProperty = DependencyProperty.Register(
        nameof(MinTileHeight), typeof(double), typeof(ResponsiveTilePanel),
        new FrameworkPropertyMetadata(270.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HeightRatioProperty = DependencyProperty.Register(
        nameof(HeightRatio), typeof(double), typeof(ResponsiveTilePanel),
        new FrameworkPropertyMetadata(0.95, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ResponsiveTilePanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinTileWidth
    {
        get => (double)GetValue(MinTileWidthProperty);
        set => SetValue(MinTileWidthProperty, value);
    }

    public double MaxTileWidth
    {
        get => (double)GetValue(MaxTileWidthProperty);
        set => SetValue(MaxTileWidthProperty, value);
    }

    public double MinTileHeight
    {
        get => (double)GetValue(MinTileHeightProperty);
        set => SetValue(MinTileHeightProperty, value);
    }

    public double HeightRatio
    {
        get => (double)GetValue(HeightRatioProperty);
        set => SetValue(HeightRatioProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private (int Columns, double Width, double Height) CalculateLayout(double availableWidth)
    {
        double spacing = Math.Max(0, Spacing);
        double min = Math.Max(1, MinTileWidth);
        double max = Math.Max(min, MaxTileWidth);

        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth))
            availableWidth = min;

        int columns = Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (min + spacing)));
        double width = (availableWidth - spacing * (columns - 1)) / columns;
        width = Math.Max(1, Math.Min(width, max));
        double height = Math.Max(MinTileHeight, width * Math.Max(0.1, HeightRatio));

        return (columns, width, height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, width, height) = CalculateLayout(availableSize.Width);
        double spacing = Math.Max(0, Spacing);

        int count = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, height));
            if (child.Visibility != Visibility.Collapsed)
                count++;
        }

        if (count == 0)
            return new Size(0, 0);

        int rows = (count + columns - 1) / columns;
        int usedColumns = Math.Min(columns, count);
        double desiredWidth = usedColumns * width + (usedColumns - 1) * spacing;
        if (!double.IsInfinity(availableSize.Width))
            desiredWidth = Math.Min(desiredWidth, availableSize.Width);

        return new Size(desiredWidth, rows * height + (rows - 1) * spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, width, height) = CalculateLayout(finalSize.Width);
        double spacing = Math.Max(0, Spacing);

        int index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            int row = index / columns;
            int column = index % columns;
            child.Arrange(new Rect(
                column * (width + spacing),
                row * (height + spacing),
                width,
                height));
            index++;
        }

        return finalSize;
    }
}
