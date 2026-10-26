namespace PingPilot.Models;

public sealed class UserSettings
{
    public int PingIntervalMilliseconds { get; set; } = 1000;
    public int PacketsPerCycle { get; set; } = 1;
    public int MiniHistorySeconds { get; set; } = 60;
    /// <summary>Длительность интервала, по которому рассчитывается средний ping (секунды).</summary>
    public int AverageIntervalSeconds { get; set; } = 3;
    public string TileVisualization { get; set; } = "Graph";
    public string Theme { get; set; } = "Dark";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; }
    /// <summary>Показывать пинг последней проверки («сейчас»). Если выключено — только средний.</summary>
    public bool ShowCurrentPing { get; set; } = true;
    /// <summary>Масштаб интерфейса (1.0 = 100%). Меняется Ctrl + колесо мыши.</summary>
    public double UiScale { get; set; } = 1.0;
}
