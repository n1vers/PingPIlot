using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PingPilot.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace PingPilot;

/// <summary>Один интервал графика потерь: сколько проверок группы было и сколько потеряно (PDO).</summary>
public sealed record GroupLossPoint(DateTime Time, int Lost, int Total);

/// <summary>Один интервал графика среднего ping группы. Value == null — ответов не было.</summary>
public sealed record GroupAveragePoint(DateTime Time, double? Value);

internal static class ChartKit
{
    public static readonly Brush FallbackText = PingPalette.Make(0x94, 0xA3, 0xB8);
    public static readonly Brush FallbackGrid = PingPalette.Make(0x33, 0x41, 0x55);

    public static FormattedText Text(FrameworkElement owner, string text, Brush brush, double size = 11) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(owner).PixelsPerDip);

    public static Brush Theme(FrameworkElement owner, string key, Brush fallback) =>
        owner.TryFindResource(key) as Brush ?? fallback;

    public static void Setup(FrameworkElement owner)
    {
        ToolTipService.SetIsEnabled(owner, true);
        ToolTipService.SetInitialShowDelay(owner, 0);
        ToolTipService.SetShowDuration(owner, int.MaxValue);
        ToolTipService.SetBetweenShowDelay(owner, 0);
    }
}

/// <summary>Общий график среднего ping и количества потерянных пакетов группы.</summary>
public class GroupCombinedChart : FrameworkElement
{
    private IReadOnlyList<GroupLossPoint> _lossPoints = Array.Empty<GroupLossPoint>();
    private IReadOnlyList<GroupAveragePoint> _averagePoints = Array.Empty<GroupAveragePoint>();
    private Rect _plot = Rect.Empty;
    private double? _hoverX;
    private int _hoverIndex = -1;
    public event Action<string?>? HoverChanged;

    public GroupCombinedChart()
    {
        ChartKit.Setup(this);
        ToolTipService.SetIsEnabled(this, false);
    }

    public void SetData(
        IReadOnlyList<GroupLossPoint> lossPoints,
        IReadOnlyList<GroupAveragePoint> averagePoints)
    {
        _lossPoints = lossPoints;
        _averagePoints = averagePoints;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        Brush axisBrush = ChartKit.Theme(this, "MutedBrush", ChartKit.FallbackText);
        Brush averageBrush = ChartKit.Theme(this, "GraphLineBrush", PingPalette.Good);
        Pen gridPen = new(ChartKit.Theme(this, "LineBrush", ChartKit.FallbackGrid), 1)
        {
            DashStyle = DashStyles.Dash
        };

        _plot = new Rect(48, 4, Math.Max(0, width - 48 - 58), Math.Max(0, height - 4 - 18));
        Rect plot = _plot;
        if (plot.Width < 60 || plot.Height < 20)
        {
            _plot = Rect.Empty;
            return;
        }

        int count = Math.Max(_lossPoints.Count, _averagePoints.Count);
        if (count == 0)
        {
            FormattedText waiting = ChartKit.Text(this, "Ждём первую проверку...", axisBrush, 11);
            dc.DrawText(waiting, new Point(plot.Left + (plot.Width - waiting.Width) / 2,
                plot.Top + (plot.Height - waiting.Height) / 2));
            return;
        }

        double maxMs = _averagePoints
            .Where(point => point.Value.HasValue)
            .Select(point => point.Value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        double maxPackets = _lossPoints.Count == 0 ? 0 : _lossPoints.Max(point => point.Lost);
        double msMax = Math.Max(60, Math.Ceiling(maxMs * 1.15 / 10) * 10);
        double packetMax = Math.Max(1, maxPackets);

        double YMs(double value) => plot.Bottom - Math.Min(value, msMax) / msMax * plot.Height;
        double YPackets(double value) => plot.Bottom - Math.Min(value, packetMax) / packetMax * plot.Height;
        double X(int index) => count > 1
            ? plot.Left + index * plot.Width / (count - 1)
            : plot.Left + plot.Width / 2;

        foreach (double value in new[] { 0, msMax / 2, msMax })
        {
            double y = YMs(value);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = ChartKit.Text(this, $"{value:0} ms", axisBrush, 10);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }

        foreach (double value in new[] { 0, packetMax / 2, packetMax })
        {
            double y = YPackets(value);
            FormattedText label = ChartKit.Text(this, $"{value:0}", axisBrush, 10);
            dc.DrawText(label, new Point(plot.Right + 6, y - label.Height / 2));
        }

        DrawLine(dc, _averagePoints, X, point => point.Value, YMs, averageBrush);
        DrawLine(dc, _lossPoints, X, point => point.Lost, YPackets, PingPalette.Poor);

        for (int i = 0; i < _averagePoints.Count; i++)
        {
            if (_averagePoints[i].Value is double value)
                dc.DrawEllipse(averageBrush, null, new Point(X(i), YMs(value)), 2.5, 2.5);
        }

        for (int i = 0; i < _lossPoints.Count; i++)
            dc.DrawEllipse(PingPalette.Poor, null, new Point(X(i), YPackets(_lossPoints[i].Lost)), 2.5, 2.5);

        if (_hoverX is double hoverX)
        {
            int index = HoverIndex(hoverX, count);
            double x = X(index);
            dc.DrawLine(new Pen(axisBrush, 1) { DashStyle = DashStyles.Dot },
                new Point(x, plot.Top), new Point(x, plot.Bottom));
            if (index < _averagePoints.Count && _averagePoints[index].Value is double average)
                dc.DrawEllipse(Brushes.Transparent, new Pen(averageBrush, 2),
                    new Point(x, YMs(average)), 6, 6);
            if (index < _lossPoints.Count)
                dc.DrawEllipse(Brushes.Transparent, new Pen(PingPalette.Poor, 2),
                    new Point(x, YPackets(_lossPoints[index].Lost)), 6, 6);
        }

        DateTime firstTime = _lossPoints.Count > 0 ? _lossPoints[0].Time : _averagePoints[0].Time;
        DateTime lastTime = _lossPoints.Count > 0 ? _lossPoints[^1].Time : _averagePoints[^1].Time;
        FormattedText first = ChartKit.Text(this, firstTime.ToString("HH:mm:ss"), axisBrush, 10);
        FormattedText last = ChartKit.Text(this, lastTime.ToString("HH:mm:ss"), axisBrush, 10);
        dc.DrawText(first, new Point(plot.Left, plot.Bottom + 3));
        dc.DrawText(last, new Point(plot.Right - last.Width, plot.Bottom + 3));
    }

    private static void DrawLine<T>(
        DrawingContext dc,
        IReadOnlyList<T> points,
        Func<int, double> x,
        Func<T, double?> value,
        Func<double, double> y,
        Brush brush)
    {
        if (points.Count == 0)
            return;

        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < points.Count; i++)
            {
                if (value(points[i]) is not double current)
                {
                    open = false;
                    continue;
                }

                Point point = new(x(i), y(current));
                if (!open)
                {
                    context.BeginFigure(point, false, false);
                    open = true;
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, 2), geometry);
    }

    private int HoverIndex(double x, int count) => count > 1
        ? Math.Clamp((int)Math.Round((x - _plot.Left) / _plot.Width * (count - 1)), 0, count - 1)
        : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_plot.IsEmpty || Math.Max(_lossPoints.Count, _averagePoints.Count) == 0 ||
            !_plot.Contains(e.GetPosition(this)))
        {
            ClearHover();
            return;
        }

        int count = Math.Max(_lossPoints.Count, _averagePoints.Count);
        double x = e.GetPosition(this).X;
        int index = HoverIndex(x, count);
        if (_hoverIndex == index)
            return;

        _hoverIndex = index;
        _hoverX = x;
        string averageText = index < _averagePoints.Count && _averagePoints[index].Value is double averageValue
            ? $"Ping: {averageValue:F0} ms"
            : "Ping: нет ответа";
        string lossText = index < _lossPoints.Count
            ? $"Потери: {_lossPoints[index].Lost}/{_lossPoints[index].Total}"
            : "Потери: нет данных";
        HoverChanged?.Invoke($"{averageText} · {lossText}");
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
        if (_hoverX != null)
        {
            _hoverX = null;
            InvalidateVisual();
        }
    }

    private void ClearHover()
    {
        if (_hoverIndex < 0)
            return;

        _hoverIndex = -1;
        HoverChanged?.Invoke(null);
    }
}

/// <summary>
/// Маленький график потерь выбранной группы: столбец на каждый интервал усреднения,
/// высота — сумма потерянных проверок (PDO) всех компьютеров группы за этот интервал.
/// </summary>
public class GroupLossChart : FrameworkElement
{
    private IReadOnlyList<GroupLossPoint> _points = Array.Empty<GroupLossPoint>();
    private Rect _plot = Rect.Empty;
    private double? _hoverX;
    private ToolTip? _hoverToolTip;

    public GroupLossChart() => ChartKit.Setup(this);

    public void SetData(IReadOnlyList<GroupLossPoint> points)
    {
        _points = points;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        Brush axisBrush = ChartKit.Theme(this, "MutedBrush", ChartKit.FallbackText);
        var gridPen = new Pen(ChartKit.Theme(this, "LineBrush", ChartKit.FallbackGrid), 1) { DashStyle = DashStyles.Dash };

        _plot = new Rect(34, 4, Math.Max(0, width - 34 - 4), Math.Max(0, height - 4 - 18));
        Rect plot = _plot;
        if (plot.Width < 40 || plot.Height < 20)
        {
            _plot = Rect.Empty;
            return;
        }

        int max = _points.Count == 0 ? 0 : _points.Max(p => p.Lost);
        int yMax = Math.Max(4, max + (max + 3) / 4);
        double Y(double v) => plot.Bottom - v / yMax * plot.Height;

        foreach (double value in new[] { 0, yMax / 2.0, yMax })
        {
            double y = Y(value);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            FormattedText label = ChartKit.Text(this, value.ToString("0"), axisBrush, 10);
            dc.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
        }

        if (_points.Count == 0)
        {
            FormattedText waiting = ChartKit.Text(this, "Ждём первую проверку...", axisBrush, 11);
            dc.DrawText(waiting, new Point(plot.Left + (plot.Width - waiting.Width) / 2, plot.Top + (plot.Height - waiting.Height) / 2));
            return;
        }

        double slot = plot.Width / _points.Count;
        double barWidth = Math.Max(1, slot * 0.7);

        for (int i = 0; i < _points.Count; i++)
        {
            GroupLossPoint point = _points[i];
            if (point.Lost <= 0)
                continue;

            double top = Y(point.Lost);
            dc.DrawRectangle(PingPalette.Poor, null,
                new Rect(plot.Left + slot * i + (slot - barWidth) / 2, top, barWidth, Math.Max(1, plot.Bottom - top)));
        }

        // Маркер наведения.
        if (_hoverX is double hx)
        {
            int hi = HoverIndex(hx);
            double cx = plot.Left + slot * (hi + 0.5);
            dc.DrawLine(new Pen(axisBrush, 1) { DashStyle = DashStyles.Dot }, new Point(cx, plot.Top), new Point(cx, plot.Bottom));
            GroupLossPoint point = _points[hi];
            double top = Y(point.Lost);
            dc.DrawRectangle(Brushes.Transparent, new Pen(axisBrush, 1.5),
                new Rect(plot.Left + slot * hi + (slot - barWidth) / 2, top,
                    barWidth, Math.Max(1, plot.Bottom - top)));
        }

        FormattedText first = ChartKit.Text(this, _points[0].Time.ToString("HH:mm:ss"), axisBrush, 10);
        FormattedText last = ChartKit.Text(this, _points[^1].Time.ToString("HH:mm:ss"), axisBrush, 10);
        dc.DrawText(first, new Point(plot.Left, plot.Bottom + 3));
        dc.DrawText(last, new Point(plot.Right - last.Width, plot.Bottom + 3));
    }

    private int HoverIndex(double x) =>
        Math.Clamp((int)((x - _plot.Left) / _plot.Width * _points.Count), 0, _points.Count - 1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_plot.IsEmpty || _points.Count == 0 || !_plot.Contains(e.GetPosition(this)))
        {
            ToolTip = null;
            return;
        }

        double x = e.GetPosition(this).X;
        GroupLossPoint point = _points[HoverIndex(x)];
        double percent = point.Total == 0 ? 0 : point.Lost * 100.0 / point.Total;
        SetHoverToolTip(
            $"{point.Time:dd.MM.yyyy HH:mm:ss}\n" +
            $"Потеряно (PDO): {point.Lost}\n" +
            $"Проверок за интервал: {point.Total}\n" +
            $"Потери: {percent:F1}%");

        _hoverX = x;
        InvalidateVisual();
    }

    private void SetHoverToolTip(string content)
    {
        _hoverToolTip ??= new ToolTip { PlacementTarget = this, StaysOpen = false };
        _hoverToolTip.Content = content;
        ToolTip = _hoverToolTip;
        _hoverToolTip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverToolTip != null)
            _hoverToolTip.IsOpen = false;
        ToolTip = null;
        if (_hoverX != null)
        {
            _hoverX = null;
            InvalidateVisual();
        }
    }
}

/// <summary>
/// Маленький график среднего ping выбранной группы (среднее по всем её компьютерам).
/// Цветные зоны те же, что на графиках компьютеров: до 15 мс, до 50 мс и выше.
/// </summary>
public class GroupAverageChart : FrameworkElement
{
    private static readonly Brush GoodZone = PingPalette.Make(0x22, 0xC5, 0x5E, 0x22);
    private static readonly Brush FairZone = PingPalette.Make(0xEA, 0xB3, 0x08, 0x22);
    private static readonly Brush PoorZone = PingPalette.Make(0xEF, 0x44, 0x44, 0x22);

    private IReadOnlyList<GroupAveragePoint> _points = Array.Empty<GroupAveragePoint>();
    private Rect _plot = Rect.Empty;
    private double? _hoverX;
    private ToolTip? _hoverToolTip;

    public GroupAverageChart() => ChartKit.Setup(this);

    public void SetData(IReadOnlyList<GroupAveragePoint> points)
    {
        _points = points;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        Brush axisBrush = ChartKit.Theme(this, "MutedBrush", ChartKit.FallbackText);
        Brush lineBrush = ChartKit.Theme(this, "GraphLineBrush", ChartKit.FallbackText);
        var gridPen = new Pen(ChartKit.Theme(this, "LineBrush", ChartKit.FallbackGrid), 1) { DashStyle = DashStyles.Dash };

        _plot = new Rect(46, 4, Math.Max(0, width - 46 - 4), Math.Max(0, height - 4 - 18));
        Rect plot = _plot;
        if (plot.Width < 40 || plot.Height < 20)
        {
            _plot = Rect.Empty;
            return;
        }

        double maxValue = _points.Where(p => p.Value.HasValue).Select(p => p.Value!.Value).DefaultIfEmpty(0).Max();
        double yMax = Math.Max(60, Math.Ceiling(maxValue * 1.15 / 10) * 10);
        double Y(double ms) => plot.Bottom - Math.Min(ms, yMax) / yMax * plot.Height;

        dc.DrawRectangle(GoodZone, null, new Rect(plot.Left, Y(PingPalette.GoodMaxMs), plot.Width, plot.Bottom - Y(PingPalette.GoodMaxMs)));
        dc.DrawRectangle(FairZone, null, new Rect(plot.Left, Y(PingPalette.FairMaxMs), plot.Width, Y(PingPalette.GoodMaxMs) - Y(PingPalette.FairMaxMs)));
        dc.DrawRectangle(PoorZone, null, new Rect(plot.Left, plot.Top, plot.Width, Y(PingPalette.FairMaxMs) - plot.Top));

        double lastLabelY = double.NegativeInfinity;
        foreach (double value in new[] { 0, PingPalette.GoodMaxMs, PingPalette.FairMaxMs, yMax })
        {
            double y = Y(value);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (Math.Abs(y - lastLabelY) < 13)
                continue;

            FormattedText label = ChartKit.Text(this, $"{value:0} ms", axisBrush, 10);
            dc.DrawText(label, new Point(plot.Left - 6 - label.Width, y - label.Height / 2));
            lastLabelY = y;
        }

        if (_points.Count == 0)
        {
            FormattedText waiting = ChartKit.Text(this, "Ждём первую проверку...", axisBrush, 11);
            dc.DrawText(waiting, new Point(plot.Left + (plot.Width - waiting.Width) / 2, plot.Top + (plot.Height - waiting.Height) / 2));
            return;
        }

        int n = _points.Count;
        double step = n > 1 ? plot.Width / (n - 1) : 0;
        double X(int i) => n > 1 ? plot.Left + i * step : plot.Right;

        // Интервалы без единого ответа — красные полосы, как на графиках компьютеров.
        double barWidth = Math.Max(step, 2);
        for (int i = 0; i < n; i++)
        {
            if (_points[i].Value.HasValue)
                continue;

            dc.DrawRectangle(PingPalette.Make(0xEF, 0x44, 0x44, 0x70), null,
                new Rect(Math.Max(plot.Left, X(i) - barWidth / 2), plot.Top, barWidth, plot.Height));
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < n; i++)
            {
                if (_points[i].Value is not double v)
                {
                    open = false;
                    continue;
                }

                var point = new Point(X(i), Y(v));
                if (!open)
                {
                    ctx.BeginFigure(point, false, false);
                    open = true;
                }
                else
                {
                    ctx.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(lineBrush, 2) { LineJoin = PenLineJoin.Round }, geometry);

        for (int i = 0; i < n; i++)
        {
            if (_points[i].Value is double value)
                dc.DrawEllipse(PingPalette.For((long)Math.Round(value)), null, new Point(X(i), Y(value)), 2.5, 2.5);
        }

        if (_points[n - 1].Value is double lastValue)
            dc.DrawEllipse(PingPalette.For((long)Math.Round(lastValue)), null, new Point(X(n - 1), Y(lastValue)), 4, 4);

        if (_hoverX is double hx)
        {
            int hi = HoverIndex(hx);
            double cx = X(hi);
            dc.DrawLine(new Pen(axisBrush, 1) { DashStyle = DashStyles.Dot }, new Point(cx, plot.Top), new Point(cx, plot.Bottom));
            if (_points[hi].Value is double hv)
                dc.DrawEllipse(Brushes.Transparent, new Pen(lineBrush, 2), new Point(cx, Y(hv)), 6, 6);
        }

        FormattedText first = ChartKit.Text(this, _points[0].Time.ToString("HH:mm:ss"), axisBrush, 10);
        FormattedText last = ChartKit.Text(this, _points[n - 1].Time.ToString("HH:mm:ss"), axisBrush, 10);
        dc.DrawText(first, new Point(plot.Left, plot.Bottom + 3));
        dc.DrawText(last, new Point(plot.Right - last.Width, plot.Bottom + 3));
    }

    private int HoverIndex(double x) =>
        _points.Count > 1
            ? Math.Clamp((int)Math.Round((x - _plot.Left) / _plot.Width * (_points.Count - 1)), 0, _points.Count - 1)
            : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_plot.IsEmpty || _points.Count == 0 || !_plot.Contains(e.GetPosition(this)))
        {
            ToolTip = null;
            return;
        }

        double x = e.GetPosition(this).X;
        GroupAveragePoint point = _points[HoverIndex(x)];
        SetHoverToolTip(
            $"{point.Time:dd.MM.yyyy HH:mm:ss}\n" +
            (point.Value is double v
                ? $"Средний ping группы: {v:F0} ms\nИнтервал усреднения: {PingStats.AverageIntervalSeconds} с"
                : "Нет ответов за интервал"));

        _hoverX = x;
        InvalidateVisual();
    }

    private void SetHoverToolTip(string content)
    {
        _hoverToolTip ??= new ToolTip { PlacementTarget = this, StaysOpen = false };
        _hoverToolTip.Content = content;
        ToolTip = _hoverToolTip;
        _hoverToolTip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverToolTip != null)
            _hoverToolTip.IsOpen = false;
        ToolTip = null;
        if (_hoverX != null)
        {
            _hoverX = null;
            InvalidateVisual();
        }
    }
}
