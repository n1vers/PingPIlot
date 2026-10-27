namespace PingPilot.Models;

/// <summary>
/// Расчёт скользящего среднего ping за заданное пользователем окно времени.
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
    /// Скользящее среднее для каждой удачной проверки. Окно заканчивается на
    /// конкретном пакете, поэтому линия меняется плавно, а не ступенями раз в
    /// длину интервала. Неудачные проверки остаются null и прерывают линию.
    /// </summary>
    public static double?[] RollingAverage(PingSample[] samples)
    {
        var result = new double?[samples.Length];
        if (samples.Length == 0)
            return result;

        long windowTicks = WindowTicks;
        var window = new Queue<PingSample>();
        long sum = 0;

        for (int i = 0; i < samples.Length; i++)
        {
            PingSample sample = samples[i];
            if (sample.LatencyMs is long ms)
            {
                window.Enqueue(sample);
                sum += ms;
            }

            long cutoff = sample.Time.Ticks - windowTicks;
            while (window.Count > 0 && window.Peek().Time.Ticks < cutoff)
            {
                PingSample expired = window.Dequeue();
                sum -= expired.LatencyMs!.Value;
            }

            if (sample.LatencyMs.HasValue && window.Count > 0)
                result[i] = (double)sum / window.Count;
        }

        return result;
    }

    /// <summary>
    /// Сводит историю к одной точке на каждый интервал: среднее удачных проверок
    /// (если удачных не было — точка без ответа). Текущий интервал тоже включается,
    /// поэтому график обновляется с каждым пакетом, а не раз в длину окна усреднения.
    /// </summary>
    public static PingSample[] ToBuckets(PingSample[] samples)
    {
        if (samples.Length == 0)
            return Array.Empty<PingSample>();

        long windowTicks = WindowTicks;
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

        return buckets.ToArray();
    }

    /// <summary>
    /// Скользящее среднее до последней проверки. Значение пересчитывается с каждым
    /// результатом ping, не ожидая окончания какого-либо фиксированного интервала.
    /// </summary>
    public static double? CurrentAverage(IReadOnlyList<PingSample> history)
    {
        if (history.Count == 0)
            return null;

        long cutoff = history[^1].Time.Ticks - WindowTicks;
        long sum = 0;
        int count = 0;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            PingSample sample = history[i];
            if (sample.Time.Ticks < cutoff)
                break;

            if (sample.LatencyMs is not long ms)
                continue;

            sum += ms;
            count++;
        }

        return count > 0 ? (double)sum / count : null;
    }
}
