using System.ComponentModel;
using System.Net;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using PingPilot.Services;

namespace PingPilot.Models;

/// <summary>
/// Состояние одного компьютера на дашборде. Создаётся при запуске приложения и
/// копит историю пинга всё время работы, независимо от того, открыто ли окно подробностей.
/// </summary>
public class DashboardTile : INotifyPropertyChanged
{
    // История хранится в памяти: до 3600 проверок (примерно 1 час при интервале 1 секунда).
    private const int MaxHistory = 3600;

    // Лишние записи обрезаются пачкой, а не по одной на каждый пакет:
    // RemoveRange(0, n) сдвигает весь список, и делать это на каждый ping дорого.
    private const int HistoryTrimSlack = 256;

    private readonly List<PingSample> _history = new();
    private int _sent;
    private int _received;
    private DateTime? _lastChecked;
    private DateTime? _lastOnline;
    private DateTime? _stateSince;
    private string? _resolvedIp;
    private int _consecutiveTimeouts;
    private int _activePingCycles;

    // Версия данных нужна, чтобы не пересчитывать среднее по нескольку раз за одно обновление.
    private int _version;
    private int _averageVersion = -1;
    private double? _averageCache;

    public DashboardTile(SavedComputer computer) => Computer = computer;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SavedComputer Computer { get; }
    public string Name => Computer.Name;
    public string Host => Computer.Host;
    public string Description => Computer.Description;

    // Всегда проверяем адрес так, как он введён. IP для DNS-имён выбирает PingService
    // и сам обновляет его, а тут IP только показывается — раньше первый найденный IP
    // «прилипал» к плитке навсегда, даже если был неверным или устарел.
    public string PingTarget => Host;

    public string IpAddressText
    {
        get
        {
            if (TryGetUsableAddress(Host, out IPAddress hostAddress))
                return hostAddress.ToString();

            return TryGetUsableAddress(_resolvedIp, out IPAddress resolvedAddress)
                ? resolvedAddress.ToString()
                : "не определён";
        }
    }
    public string IpAddressLabel => $"IP: {IpAddressText}";

    // Ручная проверка ждёт завершения всех активных автоматических циклов.
    // Автоматические циклы могут пересекаться, если прошлый пакет ещё ждёт тайм-аут:
    // это не должно сдвигать расписание отправки следующих пакетов.
    public bool Busy => Volatile.Read(ref _activePingCycles) > 0;

    public void BeginPingCycle() => Interlocked.Increment(ref _activePingCycles);

    public void EndPingCycle()
    {
        if (Interlocked.Decrement(ref _activePingCycles) < 0)
            Interlocked.Exchange(ref _activePingCycles, 0);
    }

    public bool? IsOnline { get; private set; }
    public long? LatencyMs { get; private set; }
    public string StatusText { get; private set; } = "Not checked";
    public string LatencyText { get; private set; } = "";

    /// <summary>Статус для показа в интерфейсе (по-русски). StatusText хранит исходное значение.</summary>
    public string StatusLabel => StatusText switch
    {
        "Not checked" => "Не проверялся",
        "Online" => "Онлайн",
        "Timeout" => "Тайм-аут",
        "DNS lookup failed" => "Ошибка DNS",
        "Unreachable" => "Недоступен",
        "Invalid IP address" => "Недопустимый IP",
        "No address" => "Нет адреса",
        "Check failed" => "Ошибка проверки",
        _ => StatusText
    };

    public Brush StatusBrush { get; private set; } = PingPalette.Neutral;

    /// <summary>Скользящее среднее значение за окно из настроек.</summary>
    public double? AverageLatencyMs
    {
        get
        {
            if (_averageVersion != _version)
            {
                _averageCache = PingStats.CurrentAverage(_history);
                _averageVersion = _version;
            }

            return _averageCache;
        }
    }

    public string AveragePeriodLabel => $"СРЕДНИЙ ПИНГ ({PingStats.AverageIntervalSeconds} с)";

    public bool ShowCurrentPing => PingDisplay.ShowCurrent;
    public bool RdpAvailable { get; private set; }
    public bool RdpButtonEnabled => IsOnline == true && RdpAvailable;

    public string AverageText => AverageLatencyMs is double avg ? $"{avg:F0} ms" : "—";

    public async Task<bool> UpdateRdpAvailabilityAsync()
    {
        bool available = await PingService.CheckRdpPortAsync(Host, timeoutMs: 700);
        SetRdpAvailability(available);
        return available;
    }

    public void SetRdpAvailability(bool available)
    {
        if (RdpAvailable != available)
        {
            RdpAvailable = available;
            Refresh();
        }
    }

    /// <summary>Группа цвета по среднему ping: 0 — зелёный, 1 — жёлтый, 2 — красный, 3 — серый.</summary>
    public int ColorRank => IsOnline != true ? 3 : AverageLatencyMs switch
    {
        null => 3,
        <= PingPalette.GoodMaxMs => 0,
        <= PingPalette.FairMaxMs => 1,
        _ => 2
    };

    public string LastCheckedText => _lastChecked?.ToString("HH:mm:ss") ?? "не проверялся";
    public string LastOnlineText => _lastOnline?.ToString("dd.MM HH:mm:ss") ?? "ещё не был онлайн";
    public string PacketLossText => _sent == 0
        ? "Потери: —"
        : $"Потери: {_sent - _received} ({LossPercent:F1}%)";
    public string LossPercentText => _sent == 0 ? "—" : $"{_sent - _received} ({LossPercent:F1}%)";
    public string SentReceivedText => $"{_sent} / {_received}";

    public string StateText
    {
        get
        {
            if (_stateSince is not { } since || IsOnline is null)
                return "Ещё не проверялся";

            return $"{(IsOnline == true ? "Онлайн" : "Недоступен")} уже {FormatSpan(DateTime.Now - since)}";
        }
    }

    public string LatencyStatsText
    {
        get
        {
            long min = long.MaxValue, max = long.MinValue, sum = 0;
            int count = 0;

            // Обычный цикл без LINQ: свойство вызывается при каждом обновлении плитки.
            for (int i = 0; i < _history.Count; i++)
            {
                if (_history[i].LatencyMs is not long ms)
                    continue;

                if (ms < min) min = ms;
                if (ms > max) max = ms;
                sum += ms;
                count++;
            }

            return count == 0
                ? "—"
                : $"{min} / {(double)sum / count:F0} / {max} ms";
        }
    }

    public string HistoryText => $"{_history.Count} проверок с запуска или сброса";

    /// <summary>Сколько проверок лежит в истории (без копирования самой истории).</summary>
    public int HistoryCount => _history.Count;

    public string VisualizationMode { get; private set; } = "Graph";
    public int MiniHistorySeconds { get; private set; } = 60;

    public void SetVisualizationMode(string mode)
    {
        VisualizationMode = string.Equals(mode, "Matrix", StringComparison.OrdinalIgnoreCase)
            ? "Matrix"
            : "Graph";
        Refresh();
    }

    public void SetMiniHistorySeconds(int seconds)
    {
        MiniHistorySeconds = Math.Clamp(seconds, 5, 3600);
        Refresh();
    }

    private double LossPercent => _sent == 0 ? 0 : (_sent - _received) * 100.0 / _sent;

    public string Tooltip
    {
        get
        {
            string description = string.IsNullOrWhiteSpace(Computer.Description)
                ? string.Empty
                : $"\nОписание: {Computer.Description}";

            if (_sent == 0)
                return $"Адрес: {Host}\nIP: {IpAddressText}{description}\nЕщё не проверялся";

            string checkedText = _lastChecked?.ToString("HH:mm:ss") ?? "—";
            return $"Адрес: {Host}\nIP: {IpAddressText}{description}\nПроверок: {_sent}, получено: {_received}, потери: {_sent - _received} ({LossPercent:F1}%)\nПоследняя проверка: {checkedText}\nНажмите, чтобы открыть подробности";
        }
    }

    /// <summary>Копия истории для графика.</summary>
    public PingSample[] GetHistory() => _history.ToArray();

    public PingSample[] GetMiniHistory()
    {
        DateTime cutoff = DateTime.Now - TimeSpan.FromSeconds(MiniHistorySeconds);

        // История отсортирована по времени, поэтому начало нужного окна ищем
        // бинарным поиском, а не просматриваем весь список с LINQ.
        int lo = 0, hi = _history.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_history[mid].Time < cutoff)
                lo = mid + 1;
            else
                hi = mid;
        }

        int count = _history.Count - lo;
        if (count <= 0)
            return Array.Empty<PingSample>();

        var result = new PingSample[count];
        _history.CopyTo(lo, result, 0, count);
        return result;
    }

    /// <param name="notify">
    /// false — не уведомлять интерфейс: так вызывает пакетная обработка, которая после
    /// всех результатов вызывает <see cref="Refresh"/> один раз на плитку.
    /// </param>
    public void Apply(PingResult result, DateTime? completedAt = null, bool notify = true)
    {
        DateTime now = completedAt ?? DateTime.Now;
        bool? previous = IsOnline;

        _sent++;
        _lastChecked = now;

        if (TryGetUsableAddress(result.ResolvedIp, out IPAddress resolvedAddress))
            _resolvedIp = resolvedAddress.ToString();

        if (result.Ok)
        {
            _consecutiveTimeouts = 0;
            _received++;
            _lastOnline = now;
            IsOnline = true;
            LatencyMs = result.LatencyMs;
            StatusText = "Online";
            LatencyText = result.LatencyMs.HasValue ? $"{result.LatencyMs.Value} ms" : "—";

        }
        else
        {
            // DNS-ошибка показывается сразу, если имя не удалось разрешить.
            // При известном адресе сначала показываем тайм-аут, затем «Недоступен».
            if (string.Equals(result.Status, "Timeout", StringComparison.OrdinalIgnoreCase))
            {
                _consecutiveTimeouts++;
                StatusText = _consecutiveTimeouts >= 3 ? "Unreachable" : "Timeout";
            }
            else
            {
                _consecutiveTimeouts = 0;
                StatusText = result.Status;
            }

            IsOnline = false;
            LatencyMs = null;
            LatencyText = "—";
            StatusBrush = PingPalette.Neutral;
        }

        if (previous != IsOnline)
            _stateSince = now;

        _history.Add(new PingSample(now, result.Ok ? result.LatencyMs : null, result.Status));
        if (_history.Count > MaxHistory + HistoryTrimSlack)
            _history.RemoveRange(0, _history.Count - MaxHistory);

        _version++;
        UpdateStatusBrushFromAverage();

        if (notify)
            Refresh();
    }

    /// <summary>
    /// Loads an archived session history without using the current clock or the live
    /// history limit. This creates a read-only dashboard tile for session playback.
    /// </summary>
    public void LoadHistoricalHistory(IEnumerable<PingSample> samples, string? ipAddress = null)
    {
        _history.Clear();
        _sent = 0;
        _received = 0;
        _lastChecked = null;
        _lastOnline = null;
        _stateSince = null;
        _resolvedIp = null;
        IsOnline = null;
        LatencyMs = null;
        StatusText = "Нет данных";
        LatencyText = "—";
        StatusBrush = PingPalette.Neutral;

        if (TryGetUsableAddress(ipAddress, out IPAddress resolvedAddress))
            _resolvedIp = resolvedAddress.ToString();

        bool? previous = null;
        foreach (PingSample sample in samples.OrderBy(s => s.Time))
        {
            _sent++;
            _lastChecked = sample.Time;
            _history.Add(sample);

            bool success = sample.LatencyMs.HasValue &&
                string.Equals(sample.Status, "Online", StringComparison.OrdinalIgnoreCase);

            if (success)
            {
                _received++;
                _lastOnline = sample.Time;
                IsOnline = true;
                LatencyMs = sample.LatencyMs;
                StatusText = "Online";
                LatencyText = sample.LatencyMs.HasValue ? $"{sample.LatencyMs.Value} ms" : "—";
            }
            else
            {
                IsOnline = false;
                LatencyMs = null;
                StatusText = sample.Status;
                LatencyText = "—";
                StatusBrush = PingPalette.Neutral;
            }

            if (previous != IsOnline)
                _stateSince = sample.Time;
            previous = IsOnline;
        }

        _version++;
        UpdateStatusBrushFromAverage();
        Refresh();
    }

    /// <summary>
    /// Кнопка «Сбросить статистику»: обнуляет историю, счётчики проверок и потери.
    /// В отличие от <see cref="ResetHistory"/> не трогает найденный IP и доступность RDP,
    /// чтобы плитка не мигала «не определён» до следующей проверки.
    /// </summary>
    public void ResetStatistics()
    {
        _history.Clear();
        _sent = 0;
        _received = 0;
        _consecutiveTimeouts = 0;
        _lastChecked = null;
        _lastOnline = null;
        _stateSince = null;
        IsOnline = null;
        LatencyMs = null;
        StatusText = "Not checked";
        LatencyText = "";
        StatusBrush = PingPalette.Neutral;
        _version++;
        Refresh();
    }

    /// <summary>Сбрасывает статистику (например, после смены адреса компьютера).</summary>
    public void ResetHistory()
    {
        _history.Clear();
        _sent = 0;
        _received = 0;
        _lastChecked = null;
        _lastOnline = null;
        _stateSince = null;
        _resolvedIp = null;
        IsOnline = null;
        LatencyMs = null;
        StatusText = "Not checked";
        LatencyText = "";
        StatusBrush = PingPalette.Neutral;
        _consecutiveTimeouts = 0;
        _version++;
        Refresh();
    }

    /// <summary>Пересчитывает цвет плитки после изменения окна усреднения.</summary>
    public void RefreshAverageColor()
    {
        _version++;   // окно усреднения могло измениться — кэш среднего устарел
        UpdateStatusBrushFromAverage();
        Refresh();
    }

    private void UpdateStatusBrushFromAverage()
    {
        StatusBrush = IsOnline == true && AverageLatencyMs is double average
            ? PingPalette.For((long)Math.Round(average))
            : PingPalette.Neutral;
    }

    /// <summary>Сообщает интерфейсу, что любые свойства могли измениться.</summary>
    public void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    private static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays} д {span:hh\\:mm\\:ss}"
            : span.ToString(@"hh\:mm\:ss");
    }

    private static bool TryGetUsableAddress(string? value, out IPAddress address)
    {
        address = IPAddress.None;

        if (string.IsNullOrWhiteSpace(value) ||
            !IPAddress.TryParse(value, out IPAddress? parsed) ||
            parsed == null ||
            IPAddress.Any.Equals(parsed) ||
            IPAddress.IPv6Any.Equals(parsed) ||
            IPAddress.Broadcast.Equals(parsed) ||
            IPAddress.None.Equals(parsed) ||
            IPAddress.IPv6None.Equals(parsed) ||
            parsed.IsIPv6Multicast)
        {
            return false;
        }

        byte[] bytes = parsed.GetAddressBytes();
        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            bytes[0] >= 224 && bytes[0] <= 239)
        {
            return false;
        }

        address = parsed;
        return true;
    }
}
