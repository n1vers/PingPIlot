namespace PingPilot.Models;

public class SavedComputersData
{
    public List<ComputerGroup> Groups { get; set; } = new();
    public List<SavedComputer> Computers { get; set; } = new();

    // Старые JSON-файлы без этого поля совместимы: будет использовано значение по умолчанию.
    public ScannerSettings ScanSettings { get; set; } = new();

    // Выбранная сортировка плиток на дашборде (индекс в списке сортировки).
    public int DashboardSort { get; set; }

    public UserSettings UserSettings { get; set; } = new();
}
