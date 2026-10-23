namespace PingPilot.Models;

public class ScanResult
{
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public long LatencyMs { get; set; }
}
