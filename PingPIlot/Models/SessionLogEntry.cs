using System.Text.Json.Serialization;

namespace PingPilot.Models;

/// <summary>One timestamped result produced by a PingPilot ping check.</summary>
public sealed class SessionLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public Guid ComputerId { get; set; }
    public Guid GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public string Host { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Status { get; set; } = "";
    public long? LatencyMs { get; set; }
    public bool Success { get; set; }

    [JsonIgnore]
    public string StatusText => Status switch
    {
        "Online" => "Онлайн",
        "Timeout" => "Тайм-аут",
        "DNS lookup failed" => "Ошибка DNS",
        "Unreachable" => "Недоступен",
        "Invalid IP address" => "Недопустимый IP",
        "No address" => "Нет адреса",
        "Check failed" => "Ошибка проверки",
        _ => Status
    };

    [JsonIgnore]
    public string LatencyText => LatencyMs.HasValue ? $"{LatencyMs.Value} ms" : "—";
}
