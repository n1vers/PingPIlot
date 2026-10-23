using System.Text.Json.Serialization;

namespace PingPilot.Models;

/// <summary>Metadata for one saved PingPilot monitoring session.</summary>
public sealed class MonitoringSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    public Guid GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public long EntryCount { get; set; }
    public int ComputerCount { get; set; }
    public List<SessionComputerSnapshot> Computers { get; set; } = new();

    [JsonIgnore]
    public string DurationText
    {
        get
        {
            DateTime end = EndedAt ?? DateTime.Now;
            TimeSpan duration = end - StartedAt;
            return duration.TotalDays >= 1
                ? $"{(int)duration.TotalDays} д {duration:hh\\:mm\\:ss}"
                : duration.ToString(@"hh\:mm\:ss");
        }
    }
}
