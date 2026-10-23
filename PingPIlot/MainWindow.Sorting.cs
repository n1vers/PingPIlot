using System.Windows;
using System.Windows.Controls;
using PingPilot.Models;

namespace PingPilot;

// Сортировка плиток на дашборде. Порядок пересчитывается после каждой проверки.
public partial class MainWindow
{
    // true, пока окно инициализируется: события выбора сортировки игнорируем.
    private bool _loadingSort = true;
    private bool _loadingVisualization = true;

    // Порядок элементов совпадает с пунктами списка SortCombo в XAML.
    private enum SortMode
    {
        Added,
        PingAscending,
        PingDescending,
        ColorGoodFirst,
        ColorProblemFirst,
        Name
    }

    private SortMode CurrentSort =>
        Enum.IsDefined(typeof(SortMode), SortCombo.SelectedIndex)
            ? (SortMode)SortCombo.SelectedIndex
            : SortMode.Added;

    private void InitializeSort()
    {
        int saved = Enum.IsDefined(typeof(SortMode), _data.DashboardSort) ? _data.DashboardSort : 0;

        _loadingSort = true;
        SortCombo.SelectedIndex = saved;
        _loadingSort = false;

        _loadingVisualization = true;
        TileVisualizationCombo.SelectedIndex = string.Equals(_data.UserSettings?.TileVisualization, "Matrix", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _loadingVisualization = false;
    }

    private void TileVisualizationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingVisualization || TileVisualizationCombo.SelectedItem is not ComboBoxItem item)
            return;

        string mode = item.Tag?.ToString() ?? "Graph";
        _data.UserSettings.TileVisualization = mode;
        foreach (DashboardTile tile in _tileCache.Values)
            tile.SetVisualizationMode(mode);
        SaveData();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSort)
            return;

        _data.DashboardSort = SortCombo.SelectedIndex;
        SaveData();
        ApplySort();
    }

    // Переставляем существующие плитки, не пересоздавая их: клики и подсказки не прерываются.
    private void ApplySort()
    {
        List<DashboardTile> desired = Sorted(_tiles).ToList();

        for (int i = 0; i < desired.Count; i++)
        {
            int current = _tiles.IndexOf(desired[i]);
            if (current != i)
                _tiles.Move(current, i);
        }
    }

    private IEnumerable<DashboardTile> Sorted(IEnumerable<DashboardTile> tiles)
    {
        // Порядок добавления: позиция компьютера в сохранённом списке. Он же — запасной ключ.
        Dictionary<Guid, int> order = _data.Computers
            .Select((computer, index) => (computer.Id, index))
            .ToDictionary(x => x.Id, x => x.index);

        int Added(DashboardTile t) => order.GetValueOrDefault(t.Computer.Id, int.MaxValue);

        return CurrentSort switch
        {
            // Устройства без пинга (недоступны или ещё не проверены) всегда в конце списка.
            SortMode.PingAscending => tiles
                .OrderBy(t => t.LatencyMs is null)
                .ThenBy(t => t.LatencyMs)
                .ThenBy(Added),

            SortMode.PingDescending => tiles
                .OrderBy(t => t.LatencyMs is null)
                .ThenByDescending(t => t.LatencyMs)
                .ThenBy(Added),

            // Зелёные, жёлтые, красные, серые.
            SortMode.ColorGoodFirst => tiles
                .OrderBy(t => t.ColorRank)
                .ThenBy(t => t.LatencyMs ?? long.MaxValue)
                .ThenBy(Added),

            // Серые, красные, жёлтые, зелёные: проблемные устройства сверху.
            SortMode.ColorProblemFirst => tiles
                .OrderByDescending(t => t.ColorRank)
                .ThenByDescending(t => t.LatencyMs ?? 0)
                .ThenBy(Added),

            SortMode.Name => tiles
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(Added),

            _ => tiles.OrderBy(Added)
        };
    }
}
