namespace PingPilot.Models;

/// <summary>Snapshot of one computer in the dashboard group saved into a monitoring session.</summary>
public sealed class SessionComputerSnapshot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Description { get; set; } = "";
    public string IpAddress { get; set; } = "";
}
