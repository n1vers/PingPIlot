using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PingPilot.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace PingPilot;

/// <summary>
/// Heatmap-style history: one colored dot for each ping sample.
/// Green = up to 15 ms, yellow = up to 50 ms, red = over 50 ms, gray = no reply/error.
/// Samples are laid out oldest-to-newest, left-to-right and then top-to-bottom.
/// The number of columns and rows adapts to the size of the control; when the
/// history does not fit, the oldest rows scroll out and the newest stay visible.
/// </summary>
public class PingMatrix : FrameworkElement
{
    // Целевой размер ячейки в device-independent пикселях: по нему считаем колонки и строки.
    private const double MiniCellSize = 16;
    private const double FullCellSize = 20;

    // Запасные цвета; обычно берутся из текущей темы.
    private static readonly Brush AxisBrush = PingPalette.Make(0x94, 0xA3, 0xB8);
    private static readonly Brush EmptyCellBrush = PingPalette.Make(0x2A, 0x38, 0x50);

    public static readonly DependencyProperty TileProperty = DependencyProperty.Register(
        nameof(Tile), typeof(DashboardTile), typeof(PingMatrix),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTileChanged));

    public static readonly DependencyProperty UseMiniHistoryProperty = DependencyProperty.Register(
        nameof(UseMiniHistory), typeof(bool), typeof(PingMatrix),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private PingSample[] _samples = Array.Empty<PingSample>();
    private Rect _plot;
    private double _cellWidth;
    private double _cellHeight;
    private double _dotRadius;
    private int _columns;
    private int _rows;
    private int _offset;
    private bool _isAttached;
    private readonly DispatcherTimer _renderTimer;

    public PingMatrix()
    {
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            if (IsVisible)
                InvalidateVisual();
        };
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) =>
        {
            _renderTimer.Stop();
            Detach();
        };
    }

    public DashboardTile? Tile
    {
        get => (DashboardTile?)GetValue(TileProperty);
        set => SetValue(TileProperty, value);
    }

    public bool UseMiniHistory
    {
        get => (bool)GetValue(UseMiniHistoryProperty);
        set => SetValue(UseMiniHistoryProperty, value);
    }

    private static void OnTileChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        PingMatrix matrix = (PingMatrix)dependencyObject;
        if (e.OldValue is DashboardTile oldTile)
            oldTile.PropertyChanged -= matrix.Tile_PropertyChanged;
        matrix._isAttached = false;
        if (matrix.IsLoaded)
            matrix.Attach();
        matrix.InvalidateVisual();
    }

    // Подписываемся на плитку только пока элемент в визуальном дереве, иначе
    // удалённые из списка плитки продолжают держать и перерисовывать старые элементы.
    private void Attach()
    {
        if (_isAttached || Tile == null)
            return;

        Tile.PropertyChanged += Tile_PropertyChanged;
        _isAttached = true;
        InvalidateVisual();
    }

    private void Detach()
    {
        if (!_isAttached)
            return;

        if (Tile != null)
            Tile.PropertyChanged -= Tile_PropertyChanged;
        _isAttached = false;
    }

    private void Tile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Матрица может быть создана рядом с графиком, но скрыта переключателем.
        // Не создаём очередь перерисовок, пока её не видно.
        if (!IsVisible)
            return;

        if (Dispatcher.CheckAccess())
            ScheduleRender();
        else
            Dispatcher.BeginInvoke(ScheduleRender);
    }

    private void ScheduleRender()
    {
        if (!_renderTimer.IsEnabled)
            _renderTimer.Start();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        _samples = Tile == null
            ? Array.Empty<PingSample>()
            : UseMiniHistory ? Tile.GetMiniHistory() : Tile.GetHistory();
        Brush axisBrush = ThemeBrush("MutedBrush", AxisBrush);
        Brush emptyBrush = ThemeBrush("Surface2Brush", EmptyCellBrush);

        _columns = 0;
        _rows = 0;
        _offset = 0;

        _plot = new Rect(10, 8, Math.Max(0, width - 20), Math.Max(0, height - 16));
        if (_plot.Width < 60 || _plot.Height < 30)
            return;

        // Сетка подстраивается под размер элемента: больше места — больше колонок и строк.
        double targetCell = UseMiniHistory ? MiniCellSize : FullCellSize;
        _columns = Math.Max(1, (int)(_plot.Width / targetCell));
        _rows = Math.Max(1, (int)(_plot.Height / targetCell));
        _cellWidth = _plot.Width / _columns;
        _cellHeight = _plot.Height / _rows;
        _dotRadius = Math.Min(UseMiniHistory ? 8.5 : 5.5,
            Math.Min(_cellWidth, _cellHeight) * 0.42);

        // Если история не помещается, сдвигаем её на целое число строк:
        // старые строки уходят вверх, самые свежие точки остаются видны.
        int capacity = _columns * _rows;
        if (_samples.Length > capacity)
            _offset = (int)Math.Ceiling((_samples.Length - capacity) / (double)_columns) * _columns;

        // Empty cells are useful in the full-size matrix, but make the mini
        // dashboard view look noisy, so it draws only actual samples.
        if (!UseMiniHistory)
        {
            for (int row = 0; row < _rows; row++)
            {
                for (int col = 0; col < _columns; col++)
                {
                    dc.DrawEllipse(emptyBrush, null, Center(row, col), _dotRadius, _dotRadius);
                }
            }
        }

        if (_samples.Length == 0)
        {
            FormattedText waiting = Text("Ждём первую проверку...", axisBrush);
            dc.DrawText(waiting, new Point(
                _plot.Left + Math.Max(0, (_plot.Width - waiting.Width) / 2),
                _plot.Top + Math.Max(0, (_plot.Height - waiting.Height) / 2)));
            return;
        }

        int visible = Math.Min(capacity, _samples.Length - _offset);
        for (int i = 0; i < visible; i++)
        {
            // Старые точки слева вверху, новые идут вправо, затем на следующую строку вниз.
            int row = i / _columns;
            int col = i % _columns;

            PingSample sample = _samples[_offset + i];

            Brush brush = sample.LatencyMs.HasValue
                ? PingPalette.For(sample.LatencyMs)
                : PingPalette.Neutral;

            dc.DrawEllipse(brush, null, Center(row, col), _dotRadius, _dotRadius);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_samples.Length == 0 || _columns <= 0 || _cellWidth <= 0 || _cellHeight <= 0 || !_plot.Contains(e.GetPosition(this)))
        {
            ToolTip = null;
            return;
        }

        Point point = e.GetPosition(this);
        int col = (int)((point.X - _plot.Left) / _cellWidth);
        int row = (int)((point.Y - _plot.Top) / _cellHeight);
        if (col < 0 || col >= _columns || row < 0 || row >= _rows)
        {
            ToolTip = null;
            return;
        }

        int index = _offset + row * _columns + col;
        if (index < 0 || index >= _samples.Length)
        {
            ToolTip = null;
            return;
        }

        PingSample sample = _samples[index];
        ToolTip = sample.LatencyMs.HasValue
            ? $"{sample.Time:dd.MM HH:mm:ss} — {sample.LatencyMs} ms"
            : $"{sample.Time:dd.MM HH:mm:ss} — {sample.Status}";
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ToolTip = null;
    }

    private Brush ThemeBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private Point Center(int row, int col) => new(
        _plot.Left + col * _cellWidth + _cellWidth / 2,
        _plot.Top + row * _cellHeight + _cellHeight / 2);

    private FormattedText Text(string text, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
