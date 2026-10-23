using System.Globalization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PingPilot.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using PenLineJoin = System.Windows.Media.PenLineJoin;
using Point = System.Windows.Point;

namespace PingPilot;

/// <summary>
/// График пинга по истории плитки. Цветные зоны: до 15 мс, до 50 мс и выше;
/// неудачные проверки показаны серыми полосами.
/// </summary>
public class PingGraph : FrameworkElement
{
    // Запасные цвета на случай, если ресурсы темы недоступны; обычно берутся из темы.
    private static readonly Brush AxisBrush = PingPalette.Make(0x94, 0xA3, 0xB8);
    private static readonly Brush LineBrush = PingPalette.Make(0x93, 0xC5, 0xFD);
    private static readonly Brush GridBrush = PingPalette.Make(0x33, 0x41, 0x55);
    private static readonly Brush GoodZone = PingPalette.Make(0x22, 0xC5, 0x5E, 0x22);
    private static readonly Brush FairZone = PingPalette.Make(0xEA, 0xB3, 0x08, 0x22);
    private static readonly Brush PoorZone = PingPalette.Make(0xEF, 0x44, 0x44, 0x22);
    private static readonly Brush FailBrush = PingPalette.Make(0x6B, 0x72, 0x80, 0x55);

    public static readonly DependencyProperty TileProperty = DependencyProperty.Register(
        nameof(Tile), typeof(DashboardTile), typeof(PingGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTileChanged));

    public static readonly DependencyProperty UseMiniHistoryProperty = DependencyProperty.Register(
        nameof(UseMiniHistory), typeof(bool), typeof(PingGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private PingSample[] _samples = Array.Empty<PingSample>();
    private Rect _plot;
    private bool _isAttached;

    public PingGraph()
    {
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
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
        PingGraph graph = (PingGraph)dependencyObject;
        if (e.OldValue is DashboardTile oldTile)
            oldTile.PropertyChanged -= graph.Tile_PropertyChanged;
        graph._isAttached = false;
        if (graph.IsLoaded)
            graph.Attach();
        graph.InvalidateVisual();
    }

    // Подписываемся на плитку только пока график находится в визуальном дереве,
    // иначе удалённые элементы остаются подписанными и перерисовываются зря.
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
        if (Dispatcher.CheckAccess())
            InvalidateVisual();
        else
            Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        // Прозрачный фон нужен, чтобы график получал события мыши.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        _samples = Tile == null
            ? Array.Empty<PingSample>()
            : UseMiniHistory ? Tile.GetMiniHistory() : Tile.GetHistory();
        // Цвета берём из текущей темы, чтобы график читался и на светлом, и на тёмном фоне.
        Brush axisBrush = ThemeBrush("MutedBrush", AxisBrush);
        Brush lineBrush = ThemeBrush("GraphLineBrush", LineBrush);
        var gridPen = new Pen(ThemeBrush("LineBrush", GridBrush), 1) { DashStyle = DashStyles.Dash };
        double lineThickness = UseMiniHistory ? 1.5 : 2.0;
        double fontSize = UseMiniHistory ? 10 : 12;

        var linePen = new Pen(lineBrush, lineThickness)
        {
            LineJoin = PenLineJoin.Round
        };

        // Слева оставляем место под подписи оси Y — и в мини-режиме тоже,
        // иначе подписи рисовались за границей элемента и обрезались.
        _plot = UseMiniHistory
            ? new Rect(34, 4,
                Math.Max(0, width - 34 - 4),
                Math.Max(0, height - 4 - 4))
            : new Rect(56, 12,
                Math.Max(0, width - 56 - 16),
                Math.Max(0, height - 12 - 30));
        Rect plot = _plot;

        if (plot.Width < 40 || plot.Height < 24)
        {
            // Слишком мало места: сбрасываем область, чтобы тултип не работал по старым размерам.
            _plot = Rect.Empty;
            return;
        }

        double maxLatency = _samples
            .Where(s => s.LatencyMs.HasValue)
            .Select(s => (double)s.LatencyMs!.Value)
            .DefaultIfEmpty(0)
            .Max();

        // Шкала всегда показывает пороги 15 и 50 мс.
        double yMax = Math.Max(60, Math.Ceiling(maxLatency * 1.15 / 10) * 10);

        double Y(double ms) => plot.Bottom - Math.Min(ms, yMax) / yMax * plot.Height;

        // Цветные зоны.
        dc.DrawRectangle(GoodZone, null, ZoneRect(plot, Y(PingPalette.GoodMaxMs), plot.Bottom));
        dc.DrawRectangle(FairZone, null, ZoneRect(plot, Y(PingPalette.FairMaxMs), Y(PingPalette.GoodMaxMs)));
        dc.DrawRectangle(PoorZone, null, ZoneRect(plot, plot.Top, Y(PingPalette.FairMaxMs)));

        // Подписи оси Y; слишком близкие друг к другу пропускаем.
        double lastLabelY = double.NegativeInfinity;
        foreach (double value in new[] { 0, PingPalette.GoodMaxMs, PingPalette.FairMaxMs, yMax })
        {
            double y = Y(value);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            if (Math.Abs(y - lastLabelY) < fontSize + 4)
                continue;

            FormattedText label = Text($"{value:0} ms", axisBrush, fontSize);
            dc.DrawText(label, new Point(plot.Left - 8 - label.Width, y - label.Height / 2));
            lastLabelY = y;
        }

        if (_samples.Length == 0)
        {
            FormattedText waiting = Text("Ждём первую проверку...", axisBrush, fontSize);
            dc.DrawText(waiting, new Point(
                plot.Left + Math.Max(0, (plot.Width - waiting.Width) / 2),
                plot.Top + Math.Max(0, (plot.Height - waiting.Height) / 2)));
            return;
        }

        int n = _samples.Length;
        double step = n > 1 ? plot.Width / (n - 1) : 0;
        double X(int i) => n > 1 ? plot.Left + i * step : plot.Right;

        // Неудачные проверки: серые вертикальные полосы.
        double barWidth = Math.Max(step, 2);
        for (int i = 0; i < n; i++)
        {
            if (_samples[i].LatencyMs.HasValue)
                continue;

            dc.DrawRectangle(FailBrush, null, new Rect(
                Math.Max(plot.Left, X(i) - barWidth / 2), plot.Top, barWidth, plot.Height));
        }

        // Линия пинга; при неудачной проверке линия прерывается.
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            bool figureOpen = false;
            for (int i = 0; i < n; i++)
            {
                if (_samples[i].LatencyMs is not long ms)
                {
                    figureOpen = false;
                    continue;
                }

                var point = new Point(X(i), Y(ms));
                if (!figureOpen)
                {
                    context.BeginFigure(point, false, false);
                    figureOpen = true;
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, linePen, geometry);

        // Точки окрашены по порогам; при большой истории рисуем только последнюю.
        for (int i = 0; i < n; i++)
        {
            if (_samples[i].LatencyMs is not long ms)
                continue;

            bool isLast = i == n - 1;
            if (n > 400 && !isLast)
                continue;

            double radius = UseMiniHistory
     ? (isLast ? 2.0 : 0.8)
     : (isLast ? 5.0 : 3.4);
            dc.DrawEllipse(PingPalette.For(ms), null, new Point(X(i), Y(ms)), radius, radius);
        }

        // Подписи времени есть только в полном режиме: в мини-режиме под графиком нет места.
        if (!UseMiniHistory)
        {
            FormattedText first = Text(_samples[0].Time.ToString("HH:mm:ss"), axisBrush, fontSize);
            FormattedText last = Text(_samples[n - 1].Time.ToString("HH:mm:ss"), axisBrush, fontSize);
            dc.DrawText(first, new Point(plot.Left, plot.Bottom + 9));
            dc.DrawText(last, new Point(plot.Right - last.Width, plot.Bottom + 9));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_samples.Length == 0 || _plot.IsEmpty || _plot.Width <= 0)
        {
            ToolTip = null;
            return;
        }

        double x = e.GetPosition(this).X;
        int index = _samples.Length > 1
            ? (int)Math.Round((x - _plot.Left) / _plot.Width * (_samples.Length - 1))
            : 0;
        index = Math.Clamp(index, 0, _samples.Length - 1);

        PingSample sample = _samples[index];
        ToolTip = sample.LatencyMs.HasValue
            ? $"{sample.Time:HH:mm:ss} — {sample.LatencyMs} ms"
            : $"{sample.Time:HH:mm:ss} — {sample.Status}";
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ToolTip = null;
    }

    private static Rect ZoneRect(Rect plot, double top, double bottom) =>
        new(plot.Left, top, plot.Width, Math.Max(0, bottom - top));

    private FormattedText Text(string text, Brush brush, double size = 12) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private Brush ThemeBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;
}
