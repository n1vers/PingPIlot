using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PingPilot.Models;
using PingPilot.Services;

namespace PingPilot;

/// <summary>
/// Подробности компьютера: сверху настройки и информация, снизу график пинга.
/// История для графика копится в плитке с запуска приложения, а не с открытия окна.
/// </summary>
public partial class ComputerDetailsWindow : Window
{
    private readonly DashboardTile _tile;
    private readonly Func<DashboardTile, string, string, Guid, string?> _save;
    private readonly Func<DashboardTile, bool> _delete;
    private readonly Func<DashboardTile, Task> _pingNow;
    private readonly Func<DashboardTile, Task> _rdpOpen;

    /// <param name="save">Сохраняет изменения; возвращает null при успехе или текст ошибки.</param>
    /// <param name="delete">Удаляет компьютер (с подтверждением); true — удалён.</param>
    /// <param name="pingNow">Немедленно проверяет компьютер.</param>
    /// <param name="rdpOpen">Открывает подключение по RDP после проверки доступности порта.</param>
    public ComputerDetailsWindow(
        Window owner,
        DashboardTile tile,
        IReadOnlyList<ComputerGroup> groups,
        Func<DashboardTile, string, string, Guid, string?> save,
        Func<DashboardTile, bool> delete,
        Func<DashboardTile, Task> pingNow,
        Func<DashboardTile, Task> rdpOpen)
    {
        InitializeComponent();

        Owner = owner;
        _tile = tile;
        _save = save;
        _delete = delete;
        _pingNow = pingNow;
        _rdpOpen = rdpOpen;

        var workArea = SystemParameters.WorkArea;

        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;

        // Окно подробностей масштабируется вместе с главным окном.
        UiScaleService.Attach(this, (FrameworkElement)Content);
        Width = Math.Min(Width * UiScaleService.Scale, workArea.Width);
        Height = Math.Min(Height * UiScaleService.Scale, workArea.Height);

        DataContext = tile;
        Graph.Tile = tile;
        Matrix.Tile = tile;

        HostInput.Text = tile.Host;
        DescriptionInput.Text = tile.Description;
        GroupSelect.ItemsSource = groups;
        GroupSelect.SelectedItem = groups.FirstOrDefault(g => g.Id == tile.Computer.GroupId);

        // Каждая новая проверка обновляет информацию (через привязки) и график.
        tile.PropertyChanged += OnTileChanged;
        Closed += (_, _) => tile.PropertyChanged -= OnTileChanged;
    }

    private void OnTileChanged(object? sender, PropertyChangedEventArgs e) =>
        Graph.InvalidateVisual();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Guid groupId = (GroupSelect.SelectedItem as ComputerGroup)?.Id ?? Guid.Empty;

        string? error = _save(_tile, HostInput.Text, DescriptionInput.Text, groupId);

        if (error != null)
        {
            SetStatus(error, isError: true);
            return;
        }

        // Адрес мог быть нормализован (например, обрезаны пробелы).
        HostInput.Text = _tile.Host;
        SetStatus("Сохранено.", isError: false);
    }

    private async void PingNowButton_Click(object sender, RoutedEventArgs e) =>
        await _pingNow(_tile);

    private async void RdpButton_Click(object sender, RoutedEventArgs e) =>
        await _rdpOpen(_tile);

    private void OpenGraphButton_Click(object sender, RoutedEventArgs e)
    {
        Graph.Visibility = Visibility.Visible;
        Matrix.Visibility = Visibility.Collapsed;
        ViewTitle.Text = "ГРАФИК ПИНГА";
    }

    private void OpenMatrixButton_Click(object sender, RoutedEventArgs e)
    {
        Graph.Visibility = Visibility.Collapsed;
        Matrix.Visibility = Visibility.Visible;
        ViewTitle.Text = "МАТРИЦА ПИНГА";
        Matrix.InvalidateVisual();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_delete(_tile))
            Close();
    }

    private void SetStatus(string text, bool isError)
    {
        EditStatusText.Text = text;
        EditStatusText.Foreground = isError ? PingPalette.Poor : PingPalette.Good;
    }
}
