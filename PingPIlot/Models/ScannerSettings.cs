namespace PingPilot.Models;

/// <summary>Настройки источника сканирования и периодического автосканирования.</summary>
public class ScannerSettings
{
    /// <summary>LocalNetwork или IpRange.</summary>
    public string TargetMode { get; set; } = "LocalNetwork";

    public string RangeStart { get; set; } = string.Empty;
    public string RangeEnd { get; set; } = string.Empty;

    public bool AutoScanEnabled { get; set; } = false;
    public int AutoScanIntervalSeconds { get; set; } = 60;
}
