using System.Windows.Threading;
using PingPilot.Models;

namespace PingPilot;

// Маленькие графики выбранной группы на дашборде: потери (PDO) и средний ping.
public partial class MainWindow
{
    // Максимум точек на графике. Если сессия длиннее, соседние интервалы объединяются,
    // и график всё равно показывает всю сессию целиком.
    private const int ChartPoints = 600;

    private readonly GroupChartHistory _groupHistory = new();

    private readonly DispatcherTimer _chartsTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };

    private void InitializeCharts()
    {
        GroupCombinedChartControl.HoverChanged += text => GroupChartHoverInfo.Text = text ?? "Наведи курсор на точку";

        // Пересчитываем только пока открыт дашборд: в остальное время историю не копируем впустую.
        _chartsTimer.Tick += (_, _) =>
        {
            if (DashboardTab.IsSelected)
                RefreshGroupCharts();
        };
        _chartsTimer.Start();
        Closed += (_, _) => _chartsTimer.Stop();

        MainTabs.SelectionChanged += (_, e) =>
        {
            // Событие поднимается и от вложенных списков и вкладок.
            if (ReferenceEquals(e.OriginalSource, MainTabs) && DashboardTab.IsSelected)
                RefreshGroupCharts();
        };
    }

    private void RefreshGroupCharts()
    {
        // Вызывается и во время создания окна, когда элементы ещё не готовы.
        if (GroupCombinedChartControl == null)
            return;

        string groupName = CurrentGroup?.Name ?? "—";
        GroupChartTitle.Text = $"ГРУППА «{groupName}» · средний ping и PDO, вся сессия с {_currentSession.StartedAt:HH:mm:ss}";

        SetCurrentSessionGroupChart(GroupCombinedChartControl);
    }

    // Вся текущая сессия выбранной группы: данные копятся отдельно от истории плиток
    // и журнала на экране, поэтому ничего не обрезается и график сразу полный.
    private void SetCurrentSessionGroupChart(GroupCombinedChart chart)
    {
        if (CurrentGroup is not { } group)
        {
            chart.SetData(Array.Empty<GroupLossPoint>(), Array.Empty<GroupAveragePoint>());
            return;
        }

        var (lossPoints, averagePoints) = _groupHistory.Build(group.Id, ChartPoints);
        chart.SetData(lossPoints, averagePoints);
    }

    private void SetSessionGroupChart(IEnumerable<SessionLogEntry> entries)
    {
        var (lossPoints, averagePoints) = GroupChartHistory.BuildFromEntries(entries, ChartPoints);
        SessionGroupCombinedChart.SetData(lossPoints, averagePoints);
    }
}
