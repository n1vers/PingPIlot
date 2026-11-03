using PingPilot.Models;

namespace PingPilot;

/// <summary>
/// Накопленная за всю сессию статистика по группам для общего графика (средний ping и PDO).
/// Хранится по одной ячейке на секунду, поэтому не зависит ни от лимита истории плиток,
/// ни от выбранной группы, ни от интервала усреднения: любой интервал считается из секундных ячеек.
/// </summary>
internal sealed class GroupChartHistory
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, SortedDictionary<long, Cell>> _groups = new();

    private struct Cell
    {
        public long Sum;
        public int Ok;
        public int Total;
    }

    /// <summary>Учитывает одну проверку. latencyMs == null — ответа не было.</summary>
    public void Add(Guid groupId, DateTime time, long? latencyMs)
    {
        long second = time.Ticks / TicksPerSecond;
        lock (_sync)
        {
            if (!_groups.TryGetValue(groupId, out SortedDictionary<long, Cell>? cells))
            {
                cells = new SortedDictionary<long, Cell>();
                _groups[groupId] = cells;
            }

            cells.TryGetValue(second, out Cell cell);
            cell.Total++;
            if (latencyMs is long ms)
            {
                cell.Sum += ms;
                cell.Ok++;
            }

            cells[second] = cell;
        }
    }

    public void Reset()
    {
        lock (_sync)
            _groups.Clear();
    }

    /// <summary>
    /// Строит точки графика на всю сессию группы. Шаг не меньше интервала усреднения и растёт
    /// настолько, чтобы точек было не больше <paramref name="maxPoints"/>: длинная сессия
    /// целиком помещается в график, а не обрезается до последних минут.
    /// </summary>
    public (GroupLossPoint[] Loss, GroupAveragePoint[] Average) Build(Guid groupId, int maxPoints)
    {
        KeyValuePair<long, Cell>[] cells;
        lock (_sync)
        {
            if (!_groups.TryGetValue(groupId, out SortedDictionary<long, Cell>? source) || source.Count == 0)
                return (Array.Empty<GroupLossPoint>(), Array.Empty<GroupAveragePoint>());

            cells = source.ToArray();
        }

        return Aggregate(cells.Select(pair => (pair.Key, pair.Value.Sum, pair.Value.Ok, pair.Value.Total)), maxPoints, DateTime.Now);
    }

    /// <summary>Тот же расчёт для готового журнала (сохранённая сессия).</summary>
    public static (GroupLossPoint[] Loss, GroupAveragePoint[] Average) BuildFromEntries(
        IEnumerable<SessionLogEntry> entries, int maxPoints)
    {
        var seconds = new SortedDictionary<long, Cell>();
        foreach (SessionLogEntry entry in entries)
        {
            long second = entry.Timestamp.Ticks / TicksPerSecond;
            seconds.TryGetValue(second, out Cell cell);
            cell.Total++;
            if (entry.LatencyMs is long ms)
            {
                cell.Sum += ms;
                cell.Ok++;
            }

            seconds[second] = cell;
        }

        // Сохранённая сессия уже закончилась, все интервалы в ней завершены.
        return Aggregate(seconds.Select(pair => (pair.Key, pair.Value.Sum, pair.Value.Ok, pair.Value.Total)), maxPoints, DateTime.MaxValue);
    }

    private static (GroupLossPoint[] Loss, GroupAveragePoint[] Average) Aggregate(
        IEnumerable<(long Second, long Sum, int Ok, int Total)> cells, int maxPoints, DateTime now)
    {
        (long Second, long Sum, int Ok, int Total)[] items = cells.ToArray();
        if (items.Length == 0)
            return (Array.Empty<GroupLossPoint>(), Array.Empty<GroupAveragePoint>());

        long span = Math.Max(1, PingStats.AverageIntervalSeconds);
        long totalSeconds = items[^1].Second - items[0].Second + 1;
        long neededSpan = (totalSeconds + Math.Max(1, maxPoints) - 1) / Math.Max(1, maxPoints);
        span = Math.Max(span, neededSpan);

        var loss = new List<GroupLossPoint>();
        var average = new List<GroupAveragePoint>();

        int i = 0;
        while (i < items.Length)
        {
            long bucket = items[i].Second / span;
            long sum = 0;
            int ok = 0, total = 0;
            for (; i < items.Length && items[i].Second / span == bucket; i++)
            {
                sum += items[i].Sum;
                ok += items[i].Ok;
                total += items[i].Total;
            }

            var time = new DateTime(bucket * span * TicksPerSecond);
            loss.Add(new GroupLossPoint(time, total - ok, total));
            average.Add(new GroupAveragePoint(time, ok > 0 ? (double)sum / ok : null));
        }

        // Последний интервал ещё идёт: пакеты в нём накапливаются, и линия до него «росла» бы
        // снизу вверх несколько секунд. Показываем только завершённые интервалы — график
        // меняется скачком раз в интервал, а уже нарисованные точки не двигаются.
        if (loss.Count > 1 && loss[^1].Time.AddSeconds(span) > now)
        {
            loss.RemoveAt(loss.Count - 1);
            average.RemoveAt(average.Count - 1);
        }

        return (loss.ToArray(), average.ToArray());
    }
}
