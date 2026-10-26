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

    private readonly List<PingSample> _history = new();
    private int _sent;
    private int _received;
    private DateTime? _lastChecked;
    private DateTime? _lastOnline;
    private DateTime? _stateSince;
    private string? _resolvedIp;

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

    // Не даём накладываться пингам на одну плитку.
    public bool Busy { get; set; }

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

    /// <summary>Среднее значение за интервал из настроек; null при неудачной последней проверке.</summary>
    public double? AverageLatencyMs => PingStats.CurrentAverage(_history);

    public string AveragePeriodLabel => $"СРЕДНИЙ ПИНГ ({PingStats.AverageIntervalSeconds} с)";

    public bool ShowCurrentPing => PingDisplay.ShowCurrent;
    public bool RdpAvailable { get; private set; }
    public bool RdpButtonEnabled => IsOnline == true && RdpAvailable;

    public string AverageText => AverageLatencyMs is double avg ? $"{avg:F0} ms" : "—";

    public async Task<bool> UpdateRdpAvailabilityAsync()
    {
        bool available = await PingService.CheckRdpPortAsync(Host, timeoutMs: 700);
        if (RdpAvailable != available)
        {
            RdpAvailable = available;
            Refresh();
        }
        else if (IsOnline == true)
        {
            Refresh();
        }

        return available;
    }

    /// <summary>Группа цвета: 0 — зелёный, 1 — жёлтый, 2 — красный, 3 — серый (нет пинга).</summary>
    public int ColorRank => LatencyMs switch
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
        : $"Потери: {LossPercent:F1}%";
    public string LossPercentText => _sent == 0 ? "—" : $"{LossPercent:F1}%";
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
            long[] values = _history
                .Where(s => s.LatencyMs.HasValue)
                .Select(s => s.LatencyMs!.Value)
                .ToArray();

            return values.Length == 0
                ? "—"
                : $"{values.Min()} / {values.Average():F0} / {values.Max()} ms";
        }
    }

    public string HistoryText => $"{_history.Count} проверок с запуска";

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
            return $"Адрес: {Host}\nIP: {IpAddressText}{description}\nПроверок: {_sent}, получено: {_received}, потери: {LossPercent:F1}%\nПоследняя проверка: {checkedText}\nНажмите, чтобы открыть подробности";
        }
    }

    /// <summary>Копия истории для графика.</summary>
    public PingSample[] GetHistory() => _history.ToArray();

    public PingSample[] GetMiniHistory()
    {
        DateTime cutoff = DateTime.Now - TimeSpan.FromSeconds(MiniHistorySeconds);
        return _history.Where(sample => sample.Time >= cutoff).ToArray();
    }

    public void Apply(PingResult result)
    {
        DateTime now = DateTime.Now;
        bool? previous = IsOnline;

        _sent++;
        _lastChecked = now;

        if (TryGetUsableAddress(result.ResolvedIp, out IPAddress resolvedAddress))
            _resolvedIp = resolvedAddress.ToString();

        if (result.Ok)
        {
            _received++;
            _lastOnline = now;
            IsOnline = true;
            LatencyMs = result.LatencyMs;
            StatusText = "Online";
            LatencyText = result.LatencyMs.HasValue ? $"{result.LatencyMs.Value} ms" : "—";

            // Цвет зависит от пинга: до 15 мс — зелёный, до 50 мс — жёлтый, выше — красный.
            StatusBrush = PingPalette.For(result.LatencyMs);
        }
        else
        {
            // Тайм-аут и любые ошибки — это неудачная проверка, мониторинг продолжается.
            // Устройство недоступно или проверка завершилась ошибкой — серый цвет.
            IsOnline = false;
            LatencyMs = null;
            StatusText = result.Status;
            LatencyText = "—";
            StatusBrush = PingPalette.Neutral;
        }

        if (previous != IsOnline)
            _stateSince = now;

        _history.Add(new PingSample(now, result.Ok ? result.LatencyMs : null, result.Status));
        if (_history.Count > MaxHistory)
            _history.RemoveRange(0, _history.Count - MaxHistory);

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
                StatusBrush = PingPalette.For(sample.LatencyMs);
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
        Refresh();
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
