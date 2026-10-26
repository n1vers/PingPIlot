namespace PingPilot.Models;

/// <summary>
/// Расчёт среднего ping по фиксированным временным интервалам.
/// Длительность интервала пользователь задаёт в настройках.
/// </summary>
public static class PingStats
{
    public static int AverageIntervalSeconds { get; private set; } = 3;

    private static long WindowTicks => TimeSpan.FromSeconds(AverageIntervalSeconds).Ticks;

    public static void ConfigureAverageInterval(int seconds) =>
        AverageIntervalSeconds = Math.Clamp(seconds, 1, 60);

    /// <summary>Номер временного интервала, в который попадает отметка времени.</summary>
    public static long GetBucketId(DateTime time) => time.Ticks / WindowTicks;

    /// <summary>
    /// Среднее для каждой удачной проверки внутри её временного интервала.
    /// Все удачные проверки одного интервала получают одно и то же среднее значение,
    /// поэтому график не соединяет среднее предыдущего интервала с текущим дважды.
    /// Неудачные проверки остаются null и прерывают линию.
    /// </summary>
    public static double?[] RollingAverage(PingSample[] samples)
    {
        var result = new double?[samples.Length];
        if (samples.Length == 0)
            return result;

        long windowTicks = WindowTicks;
        var totals = new Dictionary<long, (long Sum, int Count)>();

        foreach (PingSample sample in samples)
        {
            if (sample.LatencyMs is not long ms)
                continue;

            long bucket = sample.Time.Ticks / windowTicks;
            totals.TryGetValue(bucket, out var aggregate);
            totals[bucket] = (aggregate.Sum + ms, aggregate.Count + 1);
        }

        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i].LatencyMs is null)
                continue;

            long bucket = samples[i].Time.Ticks / windowTicks;
            if (totals.TryGetValue(bucket, out var aggregate) && aggregate.Count > 0)
                result[i] = (double)aggregate.Sum / aggregate.Count;
        }

        return result;
    }

    /// <summary>
    /// Сводит историю к одной точке на каждый интервал: среднее удачных проверок
    /// (если удачных не было — точка без ответа). Незавершённый последний интервал
    /// не включается, если в истории есть и завершённые интервалы.
    /// </summary>
    public static PingSample[] ToBuckets(PingSample[] samples, DateTime now)
    {
        if (samples.Length == 0)
            return Array.Empty<PingSample>();

        long windowTicks = WindowTicks;
        long nowBucket = now.Ticks / windowTicks;
        var buckets = new List<PingSample>();

        int i = 0;
        while (i < samples.Length)
        {
            long bucket = samples[i].Time.Ticks / windowTicks;
            long sum = 0;
            int count = 0;
            string status = samples[i].Status;
            int j = i;

            for (; j < samples.Length && samples[j].Time.Ticks / windowTicks == bucket; j++)
            {
                status = samples[j].Status;
                if (samples[j].LatencyMs is long ms)
                {
                    sum += ms;
                    count++;
                }
            }

            var start = new DateTime(bucket * windowTicks, samples[i].Time.Kind);
            buckets.Add(new PingSample(start, count > 0 ? (long)Math.Round((double)sum / count) : null, status));
            i = j;
        }

        if (buckets.Count > 1 && buckets[^1].Time.Ticks / windowTicks >= nowBucket)
            buckets.RemoveAt(buckets.Count - 1);

        return buckets.ToArray();
    }

    /// <summary>
    /// Среднее за последний полностью завершённый интервал.
    /// Текущий, ещё не закрывшийся интервал игнорируется, чтобы среднее не
    /// обновлялось вместе с текущим ping при включённой галке "Текущий пинг".
    /// </summary>
    public static double? CurrentAverage(IReadOnlyList<PingSample> history)
    {
        if (history.Count == 0)
            return null;

        long windowTicks = WindowTicks;
        long nowBucket = DateTime.Now.Ticks / windowTicks;
        long targetBucket = nowBucket;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            long bucket = history[i].Time.Ticks / windowTicks;
            if (bucket != nowBucket)
            {
                targetBucket = bucket;
                break;
            }
        }

        // Если в истории ещё нет полного предыдущего интервала, среднее пока не отображаем.
        if (targetBucket == nowBucket && history[^1].Time.Ticks / windowTicks == nowBucket)
            return null;

        long sum = 0;
        int count = 0;

        foreach (PingSample sample in history)
        {
            if (sample.Time.Ticks / windowTicks != targetBucket || sample.LatencyMs is not long ms)
                continue;

            sum += ms;
            count++;
        }

        return count > 0 ? (double)sum / count : null;
    }
}
