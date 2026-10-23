
namespace PingPilot.Models;

public class ComputerInfo
{
    // Основные данные компьютера
    public string Name { get; set; } = "";
    public string DnsName { get; set; } = "";
    public string IpAddress { get; set; } = "";

    // Сетевые сведения
    public string MacAddress { get; set; } = "Unknown";
    public string NetworkInterface { get; set; } = "Unknown";

    // Место размещения компьютера — используется сканером
    public string Cabinet { get; set; } = "";

    // Состояние устройства
    public string Status { get; set; } = "Not checked";
    public long? LatencyMs { get; set; }

    // Время последней проверки
    public DateTime? LastChecked { get; set; }

    // Статистика ping
    public int PacketsSent { get; set; }
    public int PacketsReceived { get; set; }

    public double PacketLossPercent =>
        PacketsSent == 0
            ? 0
            : (PacketsSent - PacketsReceived) * 100.0 / PacketsSent;

    public string LatencyText =>
        LatencyMs.HasValue ? $"{LatencyMs.Value} ms" : "—";

    public string LastCheckedText =>
        LastChecked.HasValue
            ? LastChecked.Value.ToString("HH:mm:ss")
            : "Never";

    public string PacketLossText =>
        $"{PacketLossPercent:F1}%";
}
