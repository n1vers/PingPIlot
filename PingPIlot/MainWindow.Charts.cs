using System.Windows.Threading;
using PingPilot.Models;

namespace PingPilot;

// Маленькие графики выбранной группы на дашборде: потери (PDO) и средний ping.
public partial class MainWindow
{
    // Сколько последних интервалов усреднения показывает график.
    private const int ChartPoints = 120;

    private readonly DispatcherTimer _chartsTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };

    private void InitializeCharts()
    {
        // Пересчитываем только пока открыт дашборд: в остальное время историю не копируем впустую.
        _chartsTimer.Tick += (_, _) =>
        {
            if (DashboardTab.IsSelected)
                RefreshGroupCharts();
        };
        _chartsTimer.Start();
        Closed += (_, _) => _chartsTimer.Stop();

        MainTabs.SelectionChanged += (_, e) =>
        {
            // Событие поднимается и от вложенных списков и вкладок.
            if (ReferenceEquals(e.OriginalSource, MainTabs) && DashboardTab.IsSelected)
                RefreshGroupCharts();
        };
    }

    private void RefreshGroupCharts()
    {
        // Вызывается и во время создания окна, когда элементы ещё не готовы.
        if (GroupLossChartControl == null || GroupAverageChartControl == null)
            return;

        string groupName = CurrentGroup?.Name ?? "—";
        int seconds = ChartPoints * PingStats.AverageIntervalSeconds;
        string period = seconds >= 120 ? $"{seconds / 60} мин" : $"{seconds} с";
        GroupLossTitle.Text = $"PDO ГРУППЫ «{groupName}» · сумма потерь, последние {period}";
        GroupAverageTitle.Text = $"СРЕДНИЙ PING ГРУППЫ «{groupName}» · последние {period}";

        DashboardTile[] tiles = _tiles.ToArray();
        long windowTicks = TimeSpan.FromSeconds(PingStats.AverageIntervalSeconds).Ticks;

        // Интервалы привязаны к абсолютному времени, поэтому у всех компьютеров они совпадают.
        var buckets = new SortedDictionary<long, (long Sum, int Ok, int Total)>();
        foreach (DashboardTile tile in tiles)
        {
            foreach (PingSample sample in tile.GetHistory())
            {
                long bucket = sample.Time.Ticks / windowTicks;
                buckets.TryGetValue(bucket, out (long Sum, int Ok, int Total) current);
                current.Total++;
                if (sample.LatencyMs is long ms)
                {
                    current.Sum += ms;
                    current.Ok++;
                }

                buckets[bucket] = current;
            }
        }

        var recent = buckets.TakeLast(ChartPoints).ToArray();

        GroupLossChartControl.SetData(recent
            .Select(pair => new GroupLossPoint(
                new DateTime(pair.Key * windowTicks), pair.Value.Total - pair.Value.Ok, pair.Value.Total))
            .ToArray());

        GroupAverageChartControl.SetData(recent
            .Select(pair => new GroupAveragePoint(
                new DateTime(pair.Key * windowTicks),
                pair.Value.Ok > 0 ? (double)pair.Value.Sum / pair.Value.Ok : null))
            .ToArray());
    }
}
