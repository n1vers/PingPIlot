using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using MessageBoxResult = System.Windows.MessageBoxResult;
using PingPilot.Models;
using PingPilot.Services;

namespace PingPilot;

// Дашборд: сохранённые группы, плитки компьютеров, автоматический ping.
public partial class MainWindow : Window
{
    private readonly SavedComputersService _store = new();
    private readonly ObservableCollection<DashboardTile> _tiles = new();
    private readonly Dictionary<Guid, DashboardTile> _tileCache = new();
    private readonly SemaphoreSlim _pingSemaphore = new(256, 256);
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, ComputerDetailsWindow> _detailWindows = new();
    private readonly SessionStoreService _sessionStore = new();
    private readonly ObservableCollection<MonitoringSession> _savedSessions = new();
    private readonly ObservableCollection<SessionLogEntry> _liveSessionEntries = new();
    private readonly ConcurrentQueue<(DashboardTile Tile, PingResult Result, DateTime CompletedAt, int Epoch)> _pendingPingResults = new();
    private readonly ConcurrentQueue<SessionLogEntry> _pendingLiveSessionEntries = new();

    // Номер «поколения» статистики. Кнопка «Сбросить статистику» увеличивает его, и ответы
    // пакетов, отправленных до сброса, отбрасываются, а не попадают в новую статистику.
    private int _statsEpoch;

    // Журнал сессии пишется одной цепочкой фоновых задач: порядок записей сохраняется,
    // а на каждые 100 мс не запускается новая параллельная задача.
    private Task _sessionWriteTask = Task.CompletedTask;
    private int _pendingLiveCount;

    // Порядок плиток по пингу/цвету пересчитывается не чаще раза в несколько секунд:
    // иначе плитки (с графиками внутри) переставляются и перерисовываются каждую секунду.
    private DateTime _lastSortUtc = DateTime.MinValue;
    private static readonly TimeSpan SortThrottle = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<Guid, DateTime> _lastRdpChecks = new();
    private ICollectionView? _liveSessionView;
    private readonly ObservableCollection<DashboardTile> _sessionTiles = new();
    private readonly MonitoringSession _currentSession;
    private DashboardTile? _observedSessionGraphTile;
    private bool _showingCurrentSessionView = true;
    private SavedComputersData _data = new();
    private bool _loadingGroups;
    private bool _refreshInProgress;
    private bool _allowClose;
    private int _sessionWriteErrorShown;
    private Forms.NotifyIcon? _trayIcon;
    private const int MaxLiveSessionRows = 3000;
    private const int MaxLiveSessionEntriesPerFlush = 100;
    private const int PingTimeoutMs = 800;
    private const int ForcedPingTimeoutMs = 3000;
    private static readonly TimeSpan RdpCheckInterval = TimeSpan.FromSeconds(5);
    private bool _manualCheckRunning;

    private ComputerGroup? CurrentGroup => GroupTabs.SelectedItem as ComputerGroup;

    private const double MinUiScale = 0.7;
    private const double MaxUiScale = 2.0;
    private const double UiScaleStep = 0.1;

    // Масштаб сохраняется не на каждое деление колеса, а когда пользователь закончил крутить.
    private readonly DispatcherTimer _scaleSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _pingResultUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _liveSessionUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow()
    {
        InitializeComponent();
        FitWindowToScreen();
        _scaleSaveTimer.Tick += (_, _) =>
        {
            _scaleSaveTimer.Stop();
            SaveData();
        };
        _pingResultUpdateTimer.Tick += (_, _) => FlushPingResults();
        _pingResultUpdateTimer.Start();
        _liveSessionUpdateTimer.Tick += (_, _) => FlushLiveSessionEntries();
        _liveSessionUpdateTimer.Start();

        _currentSession = _sessionStore.StartSession();
        SessionsGrid.ItemsSource = _savedSessions;
        _liveSessionView = CollectionViewSource.GetDefaultView(_liveSessionEntries);
        _liveSessionView.Filter = FilterLiveSessionEntry;
        SessionRecordsGrid.ItemsSource = _liveSessionView;
        SessionTileList.ItemsSource = _sessionTiles;
        RefreshSavedSessionsList();
        UpdateCurrentSessionHeader();

        // Подключаем эти события после InitializeComponent, чтобы не реагировать
        // на начальную инициализацию ComboBox из XAML.
        ScanTargetModeCombo.SelectionChanged += TargetModeCombo_SelectionChanged;
        MaxLatencyCombo.SelectionChanged += MaxLatencyCombo_SelectionChanged;

        _autoScanTimer = new DispatcherTimer();
        _autoScanTimer.Tick += AutoScanTimer_Tick;

        TileList.ItemsSource = _tiles;
        LoadData();
        InitializeSort();
        LoadGroups();
        LoadNetworks();
        InitializeLocalIps();
        InitializeScannerSettings();
        InitializeUserSettings();
        UpdateTrayIcon();
        ShowCurrentSessionButton_Click(this, new RoutedEventArgs());

        int intervalMs = Math.Clamp(_data.UserSettings?.PingIntervalMilliseconds ?? 1000, 100, 60000);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
        _timer.Tick += PingTimer_Tick;
        _timer.Start();

        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _autoScanTimer.Stop();
            _pingResultUpdateTimer.Stop();
            _liveSessionUpdateTimer.Stop();
            FlushLiveSessionEntries();
            if (_scaleSaveTimer.IsEnabled)
            {
                _scaleSaveTimer.Stop();
                SaveData();
            }
            _scanCts?.Cancel();
            _trayIcon?.Dispose();
            _sessionStore.Dispose();
        };
    }

    private void InitializeUserSettings()
    {
        UserSettings settings = _data.UserSettings ?? new UserSettings();
        _data.UserSettings = settings;
        PingIntervalBox.Text = Math.Clamp(settings.PingIntervalMilliseconds, 100, 60000).ToString();
        PacketsPerCycleBox.Text = Math.Clamp(settings.PacketsPerCycle, 1, 20).ToString();
        MiniHistorySecondsBox.Text = Math.Clamp(settings.MiniHistorySeconds, 5, 3600).ToString();
        settings.AverageIntervalSeconds = Math.Clamp(settings.AverageIntervalSeconds, 1, 60);
        AverageIntervalSecondsBox.Text = settings.AverageIntervalSeconds.ToString();
        PingStats.ConfigureAverageInterval(settings.AverageIntervalSeconds);
        ThemeCombo.SelectedIndex = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTray;
        PingDisplay.ShowCurrent = settings.ShowCurrentPing;
        ShowCurrentPingCheckBox.IsChecked = settings.ShowCurrentPing;
        SetUiScale(settings.UiScale, save: false);
        ApplyTheme(settings.Theme);
        foreach (DashboardTile tile in _tileCache.Values)
            tile.Refresh();
    }

    // ---------- Масштаб и размер окна ----------

    /// <summary>
    /// Подбирает стартовый размер окна по рабочей области экрана: на маленьких мониторах окно
    /// не вылезает за экран, на больших — не остаётся крошечным.
    /// </summary>
    private void FitWindowToScreen()
    {
        Rect work = SystemParameters.WorkArea;
        double width = Math.Min(work.Width * 0.95, Math.Max(1280, work.Width * 0.7));
        double height = Math.Min(work.Height * 0.95, Math.Max(800, work.Height * 0.75));
        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
    }

    private void SetUiScale(double scale, bool save = true)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            scale = 1.0;

        scale = Math.Round(Math.Clamp(scale, MinUiScale, MaxUiScale), 3);
        UiScale.ScaleX = scale;
        UiScale.ScaleY = scale;
        ZoomResetButton.Content = $"{Math.Round(scale * 100):0}%";

        // Окна подробностей и диалоги масштабируются вместе с главным окном.
        UiScaleService.Set(scale);

        if (!save || _data?.UserSettings == null || Math.Abs(_data.UserSettings.UiScale - scale) < 0.0005)
            return;

        _data.UserSettings.UiScale = scale;
        _scaleSaveTimer.Stop();
        _scaleSaveTimer.Start();
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        // Плавный масштаб: один «щелчок» колеса (120) = ±10%, а мелкие шаги тачпада
        // дают пропорционально меньшее изменение, а не прыгают сразу на 10%.
        SetUiScale(UiScale.ScaleX * Math.Pow(1 + UiScaleStep, e.Delta / 120.0));
        e.Handled = true;
    }

    private void ShowCurrentPingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool show = ShowCurrentPingCheckBox.IsChecked == true;
        PingDisplay.ShowCurrent = show;
        _data.UserSettings.ShowCurrentPing = show;
        SaveData();

        // Обновляем плитки, графики и окна подробностей.
        foreach (DashboardTile tile in _tileCache.Values)
            tile.Refresh();
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) =>
        SetUiScale(UiScale.ScaleX + UiScaleStep);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) =>
        SetUiScale(UiScale.ScaleX - UiScaleStep);

    private void ZoomResetButton_Click(object sender, RoutedEventArgs e) =>
        SetUiScale(1.0);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                SetUiScale(UiScale.ScaleX + UiScaleStep);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                SetUiScale(UiScale.ScaleX - UiScaleStep);
                break;
            case Key.D0:
            case Key.NumPad0:
                SetUiScale(1.0);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ApplyMonitoringSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PingIntervalBox.Text, out int interval) || interval is < 100 or > 60000 ||
            !int.TryParse(PacketsPerCycleBox.Text, out int packets) || packets is < 1 or > 20 ||
            !int.TryParse(MiniHistorySecondsBox.Text, out int miniHistorySeconds) || miniHistorySeconds is < 5 or > 3600 ||
            !int.TryParse(AverageIntervalSecondsBox.Text, out int averageIntervalSeconds) || averageIntervalSeconds is < 1 or > 60)
        {
            SettingsStatusText.Text = "Укажите интервал ping 100–60000 мс, пакеты 1–20, mini-историю 5–3600 секунд и среднее 1–60 секунд.";
            return;
        }

        _data.UserSettings.PingIntervalMilliseconds = interval;
        _data.UserSettings.PacketsPerCycle = packets;
        _data.UserSettings.MiniHistorySeconds = miniHistorySeconds;
        _data.UserSettings.AverageIntervalSeconds = averageIntervalSeconds;
        PingStats.ConfigureAverageInterval(averageIntervalSeconds);
        foreach (DashboardTile tile in _tileCache.Values)
        {
            tile.SetMiniHistorySeconds(miniHistorySeconds);
            tile.RefreshAverageColor();
        }
        _timer.Interval = TimeSpan.FromMilliseconds(interval);
        SaveData();
        SettingsStatusText.Text = $"Параметры применены: ping каждые {interval} мс, пакетов за интервал: {packets}, mini-история: {miniHistorySeconds} с, среднее: {averageIntervalSeconds} с.";
        _ = RefreshTilesAsync();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ThemeCombo.SelectedItem is not ComboBoxItem item)
            return;
        _data.UserSettings.Theme = item.Tag?.ToString() ?? "Dark";
        ApplyTheme(_data.UserSettings.Theme);
        SaveData();
    }

    private static void ApplyTheme(string theme)
    {
        bool light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);

        // Все ключи подключены в XAML через DynamicResource, поэтому смена темы
        // сразу обновляет и главное окно, и окна подробностей, и диалоги.
        SetBrush("BgBrush", light ? "F1F5F9" : "0F172A");
        SetBrush("SurfaceBrush", light ? "FFFFFF" : "1E293B");
        SetBrush("Surface2Brush", light ? "E2E8F0" : "2A3850");
        SetBrush("LineBrush", light ? "CBD5E1" : "334155");
        SetBrush("TextBrush", light ? "0F172A" : "F1F5F9");
        SetBrush("MutedBrush", light ? "475569" : "94A3B8");
        SetBrush("AccentBrush", light ? "2563EB" : "3B82F6");
        SetBrush("AccentHoverBrush", light ? "1D4ED8" : "2563EB");
        SetBrush("ThumbBrush", light ? "94A3B8" : "475569");
        SetBrush("DangerBrush", light ? "DC2626" : "F87171");
        SetBrush("GraphLineBrush", light ? "1D4ED8" : "93C5FD");
    }

    private static void SetBrush(string key, string hexColor)
    {
        var color = (System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString("#" + hexColor);

        // Ресурсы SolidColorBrush могут быть заморожены WPF и потому неизменяемы.
        // Заменяем ресурс целиком, а не изменяем существующий экземпляр.
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private void StartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
            if (enabled)
                key?.SetValue("PingPilot", $"\"{Environment.ProcessPath}\"");
            else
                key?.DeleteValue("PingPilot", false);
            _data.UserSettings.StartWithWindows = enabled;
            SaveData();
            SettingsStatusText.Text = enabled ? "Автозапуск включён." : "Автозапуск выключен.";
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = $"Не удалось изменить автозапуск: {ex.Message}";
        }
    }

    private void MinimizeToTrayCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _data.UserSettings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        SaveData();
        UpdateTrayIcon();
        SettingsStatusText.Text = "Настройка трея сохранена.";
    }

    private static System.Drawing.Icon GetApplicationIcon()
    {
        // Извлекаем значок из EXE: он задаётся через ApplicationIcon в csproj
        // и доступен также в single-file публикации.
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                    return icon;
            }
            catch (ArgumentException)
            {
                // Используем системный значок как резервный вариант.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Используем системный значок как резервный вариант.
            }
        }

        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private void UpdateTrayIcon()
    {
        if (_data.UserSettings.MinimizeToTray)
        {
            if (_trayIcon != null)
                return;

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть PingPilot", null, (_, _) => RestoreFromTray());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => Close());

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = GetApplicationIcon(),
                Text = "PingPilot",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
            return;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private void RestoreFromTray()
    {
        Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (_data.UserSettings.MinimizeToTray && WindowState == WindowState.Minimized)
            Visibility = Visibility.Hidden;
    }

    private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingPilot");
    private static string SessionsFolder => Path.Combine(DataFolder, "Sessions");

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(DataFolder);
    private void OpenSessionsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(SessionsFolder);

    // ---------- Данные ----------

    private void LoadData()
    {
        try
        {
            _data = _store.Load();
        }
        catch (Exception ex)
        {
            _data = new SavedComputersData();
            ShowError("Не удалось загрузить сохранённые компьютеры. Если файл повреждён, его резервная копия лежит рядом.", ex);
        }
    }

    private void SaveData()
    {
        try
        {
            _store.Save(_data);
        }
        catch (Exception ex)
        {
            ShowError("Не удалось сохранить данные. Проверьте, что есть доступ на запись в профиль пользователя.", ex);
        }
    }

    private static void ShowError(string message, Exception ex) =>
        MessageBox.Show($"{message}\n\nПодробности: {ex.Message}", "PingPilot — Ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);

    private static void ShowWarning(string message) =>
        MessageBox.Show(message, "PingPilot — Внимание",
            MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool Confirm(string question) =>
        MessageBox.Show(question, "PingPilot",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ---------- Группы и плитки ----------

    private void LoadGroups(Guid? select = null)
    {
        Guid? id = select ?? CurrentGroup?.Id;

        _loadingGroups = true;
        GroupTabs.ItemsSource = null;
        GroupTabs.ItemsSource = _data.Groups;
        GroupTabs.SelectedItem =
            _data.Groups.FirstOrDefault(g => g.Id == id) ?? _data.Groups.FirstOrDefault();
        _loadingGroups = false;

        RebuildTiles();
    }

    private DashboardTile GetOrCreateTile(SavedComputer computer)
    {
        if (_tileCache.TryGetValue(computer.Id, out DashboardTile? tile) &&
            ReferenceEquals(tile.Computer, computer))
            return tile;

        tile = new DashboardTile(computer);
        tile.SetVisualizationMode(_data.UserSettings?.TileVisualization ?? "Graph");
        tile.SetMiniHistorySeconds(_data.UserSettings?.MiniHistorySeconds ?? 60);
        _tileCache[computer.Id] = tile;
        return tile;
    }

    private void RebuildTiles()
    {
        _tiles.Clear();

        var savedIds = _data.Computers.Select(c => c.Id).ToHashSet();
        foreach (Guid staleId in _tileCache.Keys.Where(id => !savedIds.Contains(id)).ToArray())
            _tileCache.Remove(staleId);

        if (CurrentGroup is { } group)
        {
            foreach (SavedComputer computer in _data.Computers.Where(c => c.GroupId == group.Id))
                _tiles.Add(GetOrCreateTile(computer));
        }

        ApplySort();

        EmptyText.Text =
            _data.Groups.Count == 0 ? "Групп пока нет. Нажмите «+ Группа», чтобы создать первую группу, затем добавьте компьютеры."
            : _tiles.Count == 0 ? "В этой группе пока нет компьютеров. Добавьте их вручную или через сканер сети."
            : "";

        UpdateSummary();
        _ = RefreshTilesAsync();
    }

    private void UpdateSummary()
    {
        if (_data.Computers.Count == 0)
        {
            SummaryText.Text = "Компьютеров: 0";
            return;
        }

        int onlineInGroup = _tiles.Count(t => t.IsOnline == true);
        int checkedInGroup = _tiles.Count(t => t.IsOnline.HasValue);
        int onlineTotal = _tileCache.Values.Count(t => t.IsOnline == true);

        SummaryText.Text = CurrentGroup == null
            ? $"Онлайн: {onlineTotal} из {_data.Computers.Count}"
            : $"Группа: онлайн {onlineInGroup}/{_tiles.Count} · Проверено: {checkedInGroup}/{_tiles.Count} · Всего онлайн: {onlineTotal}";
    }

    // Проверяем все сохранённые компьютеры, а на дашборде показываем выбранную группу.
    private async Task RefreshTilesAsync()
    {
        if (_refreshInProgress)
            return;

        _refreshInProgress = true;
        try
        {
            DashboardTile[] allTiles = _data.Computers
                .Select(GetOrCreateTile)
                .ToArray();

            // Каждая плитка проверяется сама по себе: медленный или выключенный компьютер
            // не задерживает остальные, а пакеты внутри одного интервала распределяются равномерно.
            // Запоздавшие ответы старого цикла не сдвигают запуск следующего.
            Task[] running = allTiles
                .Select(tile => PingTileAsync(tile, allowOverlap: true))
                .ToArray();

            // Ждём быстрые ответы (не дольше 300 мс), остальные доработают в фоне.
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(300));
            ApplySortThrottled();
            UpdateSummary();
        }
        catch (Exception ex)
        {
            // RefreshTilesAsync запускается из DispatcherTimer и из нескольких
            // fire-and-forget мест. Ошибка одного цикла не должна превращаться
            // в необработанное исключение async void и прекращать мониторинг.
            Debug.WriteLine($"Ping refresh failed: {ex}");
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async void PingTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshTilesAsync();
        }
        catch (Exception ex)
        {
            // Последняя страховка для async void-обработчика WPF.
            Debug.WriteLine($"Ping timer tick failed: {ex}");
        }
    }

    // Обычная проверка по таймеру или полная (force) — «Проверить все» и «Проверить сейчас»:
    // DNS спрашивается заново, на каждый адрес до двух попыток и тайм-аут как у ping.exe.
    private async Task PingTileAsync(DashboardTile tile, bool force = false, bool allowOverlap = false)
    {
        // Ручная проверка не пропускается, если плитка занята автоматической, а ждёт её конца.
        if (force)
        {
            for (int i = 0; i < 60 && tile.Busy; i++)
                await Task.Delay(100);
        }

        if (tile.Busy && !allowOverlap)
            return;

        tile.BeginPingCycle();
        try
        {
            int packets = force ? 1 : Math.Clamp(_data.UserSettings?.PacketsPerCycle ?? 1, 1, 20);
            int timeoutMs = force ? ForcedPingTimeoutMs : PingTimeoutMs;
            int cycleIntervalMs = force
                ? 0
                : Math.Clamp(_data.UserSettings?.PingIntervalMilliseconds ?? 1000, 100, 60000);
            // Пакеты запускаются по расписанию относительно начала цикла. Ожидание
            // ответа одного пакета не задерживает следующий: например, 10 пакетов
            // за 1 с стартуют на 0, 100, …, 900 мс даже при тайм-аутах.
            Task[] scheduledPings = Enumerable.Range(0, packets)
                .Select(i => PingTilePacketAsync(
                    tile,
                    timeoutMs,
                    force,
                    TimeSpan.FromMilliseconds((double)cycleIntervalMs * i / packets)))
                .ToArray();

            if (force)
            {
                await Task.WhenAll(scheduledPings).ConfigureAwait(false);
                await UpdateRdpAvailabilityAsync(tile).ConfigureAwait(false);
            }
            else
            {
                // Не ждём медленные ответы: иначе тайм-аут последнего пакета заставит
                // пропустить следующий цикл. Результаты всё равно попадут в плитку,
                // когда придут, а следующий набор стартует ровно через интервал.
                _ = CompleteAutomaticPingCycleAsync(tile, scheduledPings);
                await Task.Delay(TimeSpan.FromMilliseconds(cycleIntervalMs)).ConfigureAwait(false);
            }
        }
        finally
        {
            await Dispatcher.InvokeAsync(tile.EndPingCycle).Task.ConfigureAwait(false);
        }
    }

    private async Task PingTilePacketAsync(
        DashboardTile tile,
        int timeoutMs,
        bool deepCheck,
        TimeSpan scheduledOffset)
    {
        // Поколение статистики запоминаем в момент постановки пакета в расписание.
        int epoch = Volatile.Read(ref _statsEpoch);

        if (scheduledOffset > TimeSpan.Zero)
            await Task.Delay(scheduledOffset).ConfigureAwait(false);

        // PingService не захватывает UI-контекст. Результат забирает общий UI-батч
        // раз в 100 мс, поэтому десятки ответов не создают очередь Dispatcher.
        await _pingSemaphore.WaitAsync().ConfigureAwait(false);
        PingResult result;
        try
        {
            // PingAsync уже асинхронный и не блокирует поток, отдельный Task.Run не нужен.
            result = await PingService.PingAsync(tile.PingTarget, timeoutMs, deepCheck)
                .ConfigureAwait(false);
        }
        finally
        {
            _pingSemaphore.Release();
        }

        _pendingPingResults.Enqueue((tile, result, DateTime.Now, epoch));
    }

    private void FlushPingResults()
    {
        if (_pendingPingResults.IsEmpty)
            return;

        int epoch = Volatile.Read(ref _statsEpoch);
        List<SessionLogEntry>? entries = null;
        List<DashboardTile>? touched = null;

        while (_pendingPingResults.TryDequeue(out var pending))
        {
            // Ответ на пакет, отправленный до сброса статистики, в новую статистику не попадает.
            if (pending.Epoch != epoch)
                continue;

            // Плитка не уведомляет интерфейс на каждый пакет: за 100 мс их приходит
            // до десятка, а перерисовка нужна одна — после всей пачки.
            pending.Tile.Apply(pending.Result, pending.CompletedAt, notify: false);

            touched ??= new List<DashboardTile>();
            if (!touched.Contains(pending.Tile))
                touched.Add(pending.Tile);

            entries ??= new List<SessionLogEntry>();
            entries.Add(CreateSessionLogEntry(pending.Tile, pending.Result, pending.CompletedAt));
        }

        if (touched != null)
        {
            foreach (DashboardTile tile in touched)
                tile.Refresh();
        }

        if (entries != null)
            QueueSessionWrite(entries);
    }

    private void QueueSessionWrite(List<SessionLogEntry> entries)
    {
        // Вызывается только из UI-потока, поэтому цепочку можно наращивать без блокировок.
        _sessionWriteTask = _sessionWriteTask.ContinueWith(
            _ =>
            {
                foreach (SessionLogEntry entry in entries)
                    RecordPingResult(entry);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private async Task CompleteAutomaticPingCycleAsync(DashboardTile tile, Task[] scheduledPings)
    {
        try
        {
            await Task.WhenAll(scheduledPings).ConfigureAwait(false);
            await UpdateRdpAvailabilityAsync(tile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Automatic ping cycle failed: {ex}");
        }
    }

    private async Task UpdateRdpAvailabilityAsync(DashboardTile tile)
    {
        bool isOnline = await Dispatcher.InvokeAsync(() => tile.IsOnline == true).Task.ConfigureAwait(false);
        if (!isOnline || !TryStartRdpCheck(tile.Computer.Id))
            return;

        bool rdpAvailable = await Task.Run(() => PingService.CheckRdpPortAsync(tile.Host, timeoutMs: 700))
            .ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() => tile.SetRdpAvailability(rdpAvailable)).Task.ConfigureAwait(false);
    }

    private bool TryStartRdpCheck(Guid computerId)
    {
        DateTime now = DateTime.UtcNow;
        while (true)
        {
            if (!_lastRdpChecks.TryGetValue(computerId, out DateTime previous))
            {
                if (_lastRdpChecks.TryAdd(computerId, now))
                    return true;
                continue;
            }

            if (now - previous < RdpCheckInterval)
                return false;

            if (_lastRdpChecks.TryUpdate(computerId, now, previous))
                return true;
        }
    }

    private async Task OpenRdpAsync(DashboardTile tile)
    {
        bool available = await tile.UpdateRdpAvailabilityAsync();
        if (!available)
        {
            ShowWarning("Подключение по RDP недоступно: порт 3389 закрыт или компьютер не отвечает.");
            return;
        }

        string target = !string.Equals(tile.IpAddressText, "не определён", StringComparison.OrdinalIgnoreCase)
            ? tile.IpAddressText
            : tile.Host;

        try
        {
            Process.Start(new ProcessStartInfo("mstsc.exe", $"/v:{target}") { UseShellExecute = true });
            SummaryText.Text = $"Запущено подключение по RDP к {target}";
        }
        catch (Exception ex)
        {
            ShowError("Не удалось открыть клиент RDP.", ex);
        }
    }

    // ---------- Сброс статистики ----------

    private async void ResetStatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(
                "Сбросить всю статистику и начать заново?\n\n" +
                "Будут очищены графики, счётчики проверок и потерь у всех компьютеров (во всех группах) " +
                "и журнал текущей сессии. Ранее сохранённые сессии останутся как есть."))
        {
            return;
        }

        ResetStatsButton.IsEnabled = false;
        // Пока идёт сброс, результаты в плитки не попадают.
        _pingResultUpdateTimer.Stop();
        _liveSessionUpdateTimer.Stop();

        try
        {
            // 1. Всё, что отправлено или принято до этого момента, в новую статистику не войдёт.
            Interlocked.Increment(ref _statsEpoch);
            while (_pendingPingResults.TryDequeue(out _))
            {
            }

            // 2. Дописываем уже принятые записи, чтобы потом они не попали в очищенный журнал.
            try
            {
                await _sessionWriteTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Session writer failed before reset: {ex}");
            }

            // 3. Журнал текущей сессии начинается заново.
            _sessionStore.ResetActiveSession(_currentSession);
            while (_pendingLiveSessionEntries.TryDequeue(out _))
            {
            }

            Interlocked.Exchange(ref _pendingLiveCount, 0);
            _liveSessionEntries.Clear();
            Interlocked.Exchange(ref _sessionWriteErrorShown, 0);

            // 4. Плитки: история, счётчики, потери, графики.
            foreach (DashboardTile tile in _tileCache.Values)
                tile.ResetStatistics();

            _lastSortUtc = DateTime.MinValue;
            ApplySortThrottled();
            UpdateSummary();
            UpdateCurrentSessionHeader();
            if (_observedSessionGraphTile != null)
                UpdateSelectedSessionComputerHeader(_observedSessionGraphTile);

            SummaryText.Text = $"Статистика сброшена в {DateTime.Now:HH:mm:ss}. Подсчёт начат заново.";
        }
        catch (Exception ex)
        {
            ShowError("Не удалось полностью сбросить статистику.", ex);
        }
        finally
        {
            _pingResultUpdateTimer.Start();
            _liveSessionUpdateTimer.Start();
            ResetStatsButton.IsEnabled = true;
        }
    }

    private void ApplySortThrottled()
    {
        // По имени и порядку добавления плитки не двигаются, а по пингу и цвету
        // порядок зависит от свежих данных и пересчитывается не чаще раза в SortThrottle.
        DateTime now = DateTime.UtcNow;
        if (now - _lastSortUtc < SortThrottle)
            return;

        _lastSortUtc = now;
        ApplySort();
    }

    // ---------- Добавление и управление ----------

    private bool EnsureGroup()
    {
        if (_data.Groups.Count > 0)
            return true;

        return AddGroup() != null;
    }

    private ComputerGroup? AddGroup()
    {
        string? name = Dialogs.AskGroupName(this);
        if (name == null)
            return null;

        if (_data.Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowWarning("Группа с таким названием уже существует.");
            return null;
        }

        var group = new ComputerGroup { Name = name };
        _data.Groups.Add(group);
        SaveData();
        LoadGroups(group.Id);
        return group;
    }

    private int AddComputers(IEnumerable<SavedComputer> items)
    {
        int added = 0;

        foreach (SavedComputer pc in items)
        {
            bool exists = _data.Computers.Any(c =>
                c.GroupId == pc.GroupId &&
                string.Equals(c.Host, pc.Host, StringComparison.OrdinalIgnoreCase));

            if (exists)
                continue;

            _data.Computers.Add(pc);
            added++;
        }

        if (added > 0)
            SaveData();

        return added;
    }

    private void GroupTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGroups)
            return;

        RebuildTiles();
        if (_showingCurrentSessionView)
            RefreshCurrentSessionView();
    }

    private void AddGroupButton_Click(object sender, RoutedEventArgs e) => AddGroup();

    private void RenameGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGroup is not { } group)
        {
            ShowWarning("Сначала выберите группу.");
            return;
        }

        string? name = Dialogs.AskGroupName(this, group.Name);
        if (name == null || name == group.Name)
            return;

        if (_data.Groups.Any(g => g.Id != group.Id &&
            string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowWarning("Группа с таким названием уже существует.");
            return;
        }

        group.Name = name;
        SaveData();
        LoadGroups(group.Id);
    }

    private void DeleteGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGroup is not { } group)
            return;

        int count = _data.Computers.Count(c => c.GroupId == group.Id);
        string question = count == 0
            ? $"Удалить группу «{group.Name}»?"
            : $"Удалить группу «{group.Name}» вместе с компьютерами ({count})?";

        if (!Confirm(question))
            return;

        _data.Computers.RemoveAll(c => c.GroupId == group.Id);
        _data.Groups.Remove(group);
        SaveData();
        LoadGroups();
    }

    private void AddComputerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGroup())
            return;

        // Окно остаётся открытым, пока пользователь добавляет компьютеры подряд.
        Dialogs.AddComputersContinuously(this, _data.Groups, CurrentGroup?.Id, TryAddComputer);
    }

    // Возвращает null при успехе или текст ошибки для окна добавления.
    private string? TryAddComputer(SavedComputer computer)
    {
        if (AddComputers(new[] { computer }) == 0)
            return $"'{computer.Host}' is already in this group.";

        LoadGroups(computer.GroupId);
        return null;
    }

    private async void TilePingNow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DashboardTile tile })
            return;

        await PingTileAsync(tile, force: true);
        ApplySort();
        UpdateSummary();
    }

    private async void TileRdp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DashboardTile tile })
            return;

        await OpenRdpAsync(tile);
    }

    private void TileCopyHost_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DashboardTile tile })
            return;

        try
        {
            Clipboard.SetText(tile.Host);
            SummaryText.Text = $"Адрес {tile.Host} скопирован";
        }
        catch (Exception ex)
        {
            ShowError("Не удалось скопировать адрес в буфер обмена.", ex);
        }
    }

    // ---------- Подробности компьютера ----------

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DashboardTile tile })
            OpenDetails(tile);
    }

    private void TileEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DashboardTile tile })
            OpenDetails(tile);
    }

    private void OpenDetails(DashboardTile tile)
    {
        Guid id = tile.Computer.Id;

        // Одно окно на компьютер: повторный клик просто показывает уже открытое.
        if (_detailWindows.TryGetValue(id, out ComputerDetailsWindow? existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;

            existing.Activate();
            return;
        }

        var window = new ComputerDetailsWindow(
            this,
            tile,
            _data.Groups,
            ApplyComputerChanges,
            DeleteComputer,
            async t =>
            {
                await PingTileAsync(t, force: true);
                ApplySort();
                UpdateSummary();
            },
            async t => await OpenRdpAsync(t));

        _detailWindows[id] = window;
        window.Closed += (_, _) => _detailWindows.Remove(id);
        window.Show();
    }

    // Меняем компьютер на месте, чтобы плитка и накопленная история пинга сохранились.
    // Возвращает null при успехе или текст ошибки.
    private string? ApplyComputerChanges(DashboardTile tile, string host, string description, Guid groupId)
    {
        host = host.Trim();

        if (host.Length == 0)
            return "Введите IP-адрес или DNS-имя.";

        if (_data.Groups.All(g => g.Id != groupId))
            return "Выберите группу.";

        SavedComputer computer = tile.Computer;

        bool duplicate = _data.Computers.Any(c =>
            c.Id != computer.Id &&
            c.GroupId == groupId &&
            string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase));

        if (duplicate)
            return "В этой группе уже есть компьютер с таким адресом.";

        bool hostChanged = !string.Equals(computer.Host, host, StringComparison.OrdinalIgnoreCase);

        // Имя, заданное раньше вручную или сканером, сохраняем; иначе имя следует за адресом.
        bool hasCustomName =
            !string.IsNullOrWhiteSpace(computer.Name) &&
            !string.Equals(computer.Name, computer.Host, StringComparison.Ordinal);

        if (!hasCustomName)
            computer.Name = host;

        computer.Host = host;
        computer.Description = description.Trim();
        computer.GroupId = groupId;

        SaveData();

        // Статистика относилась к старому адресу — начинаем заново.
        if (hostChanged)
            tile.ResetHistory();
        else
            tile.Refresh();

        LoadGroups(CurrentGroup?.Id);
        return null;
    }

    // Спрашивает подтверждение и удаляет компьютер; true — удалён.
    private bool DeleteComputer(DashboardTile tile)
    {
        if (!Confirm($"Удалить компьютер «{tile.Name}»?"))
            return false;

        _data.Computers.Remove(tile.Computer);
        _tileCache.Remove(tile.Computer.Id);
        SaveData();
        LoadGroups();
        return true;
    }

    private void TileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DashboardTile tile })
            return;

        Guid id = tile.Computer.Id;

        if (DeleteComputer(tile) && _detailWindows.TryGetValue(id, out ComputerDetailsWindow? window))
            window.Close();
    }

    // «Проверить все»: настоящая повторная проверка всех компьютеров, а не пропуск цикла,
    // когда автоматическая проверка уже идёт (она идёт почти всегда).
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_manualCheckRunning)
            return;

        _manualCheckRunning = true;
        RefreshAllButton.IsEnabled = false;
        RefreshAllButton.Content = "Проверяю…";

        try
        {
            // Даём закончиться текущему автоматическому циклу (не дольше 10 секунд).
            for (int i = 0; i < 100 && _refreshInProgress; i++)
                await Task.Delay(100);

            // На время ручной проверки автоматические тики пропускаются.
            _refreshInProgress = true;
            try
            {
                // Забываем найденные по DNS адреса: имена будут разрешены заново.
                PingService.ClearDnsCache();

                DashboardTile[] all = _data.Computers.Select(GetOrCreateTile).ToArray();
                int done = 0;

                await Task.WhenAll(all.Select(async tile =>
                {
                    await PingTileAsync(tile, force: true);
                    done++;
                    RefreshAllButton.Content = $"Проверяю… {done}/{all.Length}";
                }));

                ApplySort();
                UpdateSummary();
            }
            finally
            {
                _refreshInProgress = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Manual check failed: {ex}");
        }
        finally
        {
            RefreshAllButton.Content = "Проверить все";
            RefreshAllButton.IsEnabled = true;
            _manualCheckRunning = false;
        }
    }

    // ---------- Сессии мониторинга ----------

    private SessionLogEntry CreateSessionLogEntry(DashboardTile tile, PingResult result, DateTime timestamp)
    {
        string ipAddress = result.ResolvedIp;
        if (string.IsNullOrWhiteSpace(ipAddress) &&
            !string.Equals(tile.IpAddressText, "не определён", StringComparison.OrdinalIgnoreCase))
        {
            ipAddress = tile.IpAddressText;
        }

        return new SessionLogEntry
        {
            Timestamp = timestamp,
            ComputerId = tile.Computer.Id,
            GroupId = tile.Computer.GroupId,
            GroupName = _data.Groups.FirstOrDefault(g => g.Id == tile.Computer.GroupId)?.Name ?? "",
            ComputerName = tile.Name,
            Host = tile.Host,
            IpAddress = ipAddress,
            Status = result.Status,
            LatencyMs = result.Ok ? result.LatencyMs : null,
            Success = result.Ok
        };
    }

    private void RecordPingResult(SessionLogEntry entry)
    {

        try
        {
            // Журнал записывается построчно на диск, поэтому длинная сессия
            // не обязана целиком храниться в оперативной памяти.
            _sessionStore.Append(_currentSession, entry);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _sessionWriteErrorShown, 1) == 0)
            {
                Dispatcher.BeginInvoke(new Action(() => ShowError(
                    "Не удалось записать журнал текущей сессии. Новые записи могут не сохраниться.", ex)));
            }
        }

        _pendingLiveSessionEntries.Enqueue(entry);
        Interlocked.Increment(ref _pendingLiveCount);
        // Полный журнал уже записан на диск; в памяти для таблицы держим только
        // последние записи, чтобы мониторинг не накапливал очередь во время работы
        // на вкладке дашборда. Размер считаем счётчиком: ConcurrentQueue.Count дорогой.
        while (Volatile.Read(ref _pendingLiveCount) > MaxLiveSessionRows &&
               _pendingLiveSessionEntries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingLiveCount);
        }
    }

    private void FlushLiveSessionEntries()
    {
        // Пока вкладка сессий не открыта, журнал уже надёжно пишется на диск,
        // поэтому не создаём десятки уведомлений DataGrid на дашборде.
        if (!SessionsTab.IsSelected)
            return;

        int added = 0;
        while (added < MaxLiveSessionEntriesPerFlush &&
               _pendingLiveSessionEntries.TryDequeue(out SessionLogEntry? entry))
        {
            Interlocked.Decrement(ref _pendingLiveCount);
            _liveSessionEntries.Insert(0, entry);
            added++;
        }

        while (_liveSessionEntries.Count > MaxLiveSessionRows)
            _liveSessionEntries.RemoveAt(_liveSessionEntries.Count - 1);

        if (added > 0 && _showingCurrentSessionView)
            UpdateCurrentSessionHeader();
    }

    private bool FilterLiveSessionEntry(object item) =>
        item is SessionLogEntry entry && CurrentGroup is { } group && entry.GroupId == group.Id;

    private void UpdateCurrentSessionHeader()
    {
        string groupName = CurrentGroup?.Name ?? "не выбрана";
        SessionLogEntry[] entries = CurrentGroup == null
            ? Array.Empty<SessionLogEntry>()
            : _liveSessionEntries.Where(entry => entry.GroupId == CurrentGroup.Id).ToArray();
        int visibleEntries = entries.Length;
        string stats = FormatSessionStats(entries);

        SessionViewHeaderText.Text =
            $"Текущая сессия · группа «{groupName}» · начата {_currentSession.StartedAt:dd.MM.yyyy HH:mm:ss} · " +
            $"событий группы в текущем журнале: {visibleEntries} · всего записей в активной сессии: {_currentSession.EntryCount} · {stats}. " +
            "При сохранении останется только выбранная группа.";
    }

    private static string FormatSessionStats(IEnumerable<SessionLogEntry> source)
    {
        SessionLogEntry[] entries = source.ToArray();
        int successful = entries.Count(entry => entry.Success);
        long[] latencies = entries.Where(entry => entry.LatencyMs.HasValue).Select(entry => entry.LatencyMs!.Value).ToArray();
        if (entries.Length == 0)
            return "успешных: 0, потери: —, задержка: —";

        double loss = (entries.Length - successful) * 100.0 / entries.Length;
        string latency = latencies.Length == 0
            ? "—"
            : $"{latencies.Min()} / {latencies.Average():F0} / {latencies.Max()} мс (мин/сред/макс)";
        return $"успешных: {successful}/{entries.Length}, потери: {loss:F1}%, задержка: {latency}";
    }

    private void RefreshCurrentSessionView()
    {
        if (!_showingCurrentSessionView)
            return;

        SessionTileList.ItemsSource = _tiles;
        SessionRecordsGrid.ItemsSource = _liveSessionView;
        _liveSessionView?.Refresh();

        if (_observedSessionGraphTile == null || !_tiles.Contains(_observedSessionGraphTile))
            SetSessionGraphTile(_tiles.FirstOrDefault());
        else
            UpdateSelectedSessionComputerHeader(_observedSessionGraphTile);

        UpdateCurrentSessionHeader();
    }

    private void SetSessionGraphTile(DashboardTile? tile)
    {
        if (_observedSessionGraphTile != null)
            _observedSessionGraphTile.PropertyChanged -= SessionGraphTile_PropertyChanged;

        _observedSessionGraphTile = tile;
        SessionGraph.Tile = tile;

        if (tile != null)
            tile.PropertyChanged += SessionGraphTile_PropertyChanged;

        UpdateSelectedSessionComputerHeader(tile);
        SessionGraph.InvalidateVisual();
    }

    private void SessionGraphTile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // График сам подписан на плитку и перерисовывается с ограничением частоты, а подпись
        // нужна только пока открыта вкладка «Сессии». Раньше она собиралась на каждый пакет.
        if (!SessionsTab.IsSelected)
            return;

        UpdateSelectedSessionComputerHeader(_observedSessionGraphTile);
    }

    private void UpdateSelectedSessionComputerHeader(DashboardTile? tile)
    {
        if (tile == null)
        {
            SessionSelectedComputerText.Text = "Выберите компьютер в матрице, чтобы посмотреть его график";
            return;
        }

        SessionSelectedComputerText.Text =
            $"{tile.Name} · {tile.IpAddressText} · {tile.StatusText} · {tile.LatencyText} · {tile.PacketLossText} · " +
            $"Проверок: {tile.SentReceivedText} · История: {tile.HistoryCount}";
    }

    private void BuildSessionMatrix(MonitoringSession session, IReadOnlyList<SessionLogEntry> entries)
    {
        _sessionTiles.Clear();
        var snapshots = session.Computers?.ToList() ?? new List<SessionComputerSnapshot>();

        // Fill any missing snapshots from the records. This also preserves a computer
        // that was removed from the live group after producing historical samples.
        var knownComputerIds = snapshots.Select(snapshot => snapshot.Id).ToHashSet();
        foreach (IGrouping<Guid, SessionLogEntry> pcEntries in entries.GroupBy(entry => entry.ComputerId))
        {
            if (!knownComputerIds.Add(pcEntries.Key))
                continue;

            SessionLogEntry newest = pcEntries.OrderByDescending(entry => entry.Timestamp).First();
            snapshots.Add(new SessionComputerSnapshot
            {
                Id = pcEntries.Key,
                Name = string.IsNullOrWhiteSpace(newest.ComputerName) ? newest.Host : newest.ComputerName,
                Host = newest.Host,
                Description = "",
                IpAddress = pcEntries.OrderByDescending(entry => entry.Timestamp)
                    .Select(entry => entry.IpAddress)
                    .FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip)) ?? ""
            });
        }

        foreach (SessionComputerSnapshot snapshot in snapshots)
        {
            var computer = new SavedComputer
            {
                Id = snapshot.Id,
                Name = string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.Host : snapshot.Name,
                Host = snapshot.Host,
                Description = snapshot.Description,
                GroupId = session.GroupId
            };

            var tile = new DashboardTile(computer);
            List<SessionLogEntry> pcEntries = entries
                .Where(entry => entry.ComputerId == snapshot.Id)
                .OrderBy(entry => entry.Timestamp)
                .ToList();

            string latestIp = pcEntries
                .OrderByDescending(entry => entry.Timestamp)
                .Select(entry => entry.IpAddress)
                .FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip))
                ?? snapshot.IpAddress;

            PingSample[] samples = pcEntries
                .Select(entry => new PingSample(
                    entry.Timestamp,
                    entry.Success ? entry.LatencyMs : null,
                    entry.Status))
                .ToArray();

            tile.LoadHistoricalHistory(samples, latestIp);
            _sessionTiles.Add(tile);
        }

        SessionTileList.ItemsSource = _sessionTiles;
        SetSessionGraphTile(_sessionTiles.FirstOrDefault());
    }

    private void SessionTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DashboardTile tile })
            SetSessionGraphTile(tile);
    }

    private void RefreshSavedSessionsList()
    {
        Guid? selectedId = (SessionsGrid.SelectedItem as MonitoringSession)?.Id;
        IReadOnlyList<MonitoringSession> sessions = _sessionStore.LoadSavedSessions();

        _savedSessions.Clear();
        foreach (MonitoringSession session in sessions.OrderByDescending(s => s.StartedAt))
            _savedSessions.Add(session);

        if (selectedId.HasValue)
            SessionsGrid.SelectedItem = _savedSessions.FirstOrDefault(s => s.Id == selectedId.Value);
    }

    private async void SessionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not MonitoringSession session)
            return;

        _showingCurrentSessionView = false;
        SessionViewHeaderText.Text = $"Загрузка сессии от {session.StartedAt:dd.MM.yyyy HH:mm:ss}…";
        try
        {
            IReadOnlyList<SessionLogEntry> entries =
                await Task.Run(() => _sessionStore.LoadEntries(session.Id));

            // Пользователь мог выбрать другую сессию, пока выполнялась загрузка.
            if (SessionsGrid.SelectedItem is not MonitoringSession selected || selected.Id != session.Id)
                return;

            SessionRecordsGrid.ItemsSource = entries;
            BuildSessionMatrix(session, entries);
            string groupName = string.IsNullOrWhiteSpace(session.GroupName)
                ? entries.FirstOrDefault()?.GroupName ?? "не указана"
                : session.GroupName;
            SessionViewHeaderText.Text =
                $"Сессия от {session.StartedAt:dd.MM.yyyy HH:mm:ss} · группа: {groupName} · " +
                $"записей: {entries.Count} · компьютеров: {session.ComputerCount} · длительность: {session.DurationText} · " +
                FormatSessionStats(entries);
        }
        catch (Exception ex)
        {
            ShowError("Не удалось открыть сессию.", ex);
        }
    }

    private void ShowCurrentSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _showingCurrentSessionView = true;
        SessionsGrid.SelectedItem = null;
        RefreshCurrentSessionView();
    }

    private void RefreshSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSavedSessionsList();
    }

    private void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsGrid.SelectedItem is not MonitoringSession session)
        {
            ShowWarning("Сначала выберите сохранённую сессию.");
            return;
        }

        if (!Confirm($"Удалить сессию от {session.StartedAt:dd.MM.yyyy HH:mm:ss} с {session.EntryCount} записями?"))
            return;

        try
        {
            _sessionStore.DeleteSession(session.Id);
            RefreshSavedSessionsList();
            ShowCurrentSessionButton_Click(sender, e);
        }
        catch (Exception ex)
        {
            ShowError("Не удалось удалить сессию.", ex);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        // Дожидаемся, пока фоновая запись допишет уже принятые результаты в журнал.
        try
        {
            _sessionWriteTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Session writer failed: {ex}");
        }

        if (_currentSession.EntryCount == 0)
        {
            try
            {
                _sessionStore.DiscardSession(_currentSession);
                _allowClose = true;
            }
            catch (Exception ex)
            {
                ShowError("Не удалось закрыть временный журнал сессии.", ex);
                e.Cancel = true;
            }
            return;
        }

        ComputerGroup? group = CurrentGroup;
        if (group == null)
        {
            MessageBoxResult noGroupChoice = MessageBox.Show(
                this,
                "Сейчас на дашборде не выбрана группа, поэтому сессию нельзя сохранить только для одного профиля. Закрыть без сохранения?",
                "PingPilot — нет выбранной группы",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (noGroupChoice != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            try
            {
                _sessionStore.DiscardSession(_currentSession);
                _allowClose = true;
            }
            catch (Exception ex)
            {
                ShowError("Не удалось удалить временную сессию.", ex);
                e.Cancel = true;
            }
            return;
        }

        try
        {
            (long EntryCount, int ComputerCount) summary = _sessionStore.GetActiveSummary(group.Id);
            List<SessionComputerSnapshot> computers = _data.Computers
                .Where(computer => computer.GroupId == group.Id)
                .Select(computer =>
                {
                    DashboardTile tile = GetOrCreateTile(computer);
                    return new SessionComputerSnapshot
                    {
                        Id = computer.Id,
                        Name = computer.Name,
                        Host = computer.Host,
                        Description = computer.Description,
                        IpAddress = string.Equals(tile.IpAddressText, "не определён", StringComparison.OrdinalIgnoreCase)
                            ? ""
                            : tile.IpAddressText
                    };
                })
                .ToList();

            MessageBoxResult choice = MessageBox.Show(
                this,
                $"Сохранить сессию мониторинга только для выбранной группы «{group.Name}»?\n\n" +
                $"Начало: {_currentSession.StartedAt:dd.MM.yyyy HH:mm:ss}\n" +
                $"Группа: {group.Name}\n" +
                $"Событий в этой группе: {summary.EntryCount}\n" +
                $"Компьютеров в матрице: {computers.Count}\n\n" +
                "Графики и историю каждого компьютера этой группы можно будет открыть позже.\n\n" +
                "Да — сохранить выбранную группу.\nНет — закрыть без сохранения.\nОтмена — вернуться в программу.",
                "PingPilot — сохранение сессии",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (choice == MessageBoxResult.Yes)
            {
                _sessionStore.SaveSession(_currentSession, group.Id, group.Name, computers);
            }
            else
            {
                _sessionStore.DiscardSession(_currentSession);
            }

            _allowClose = true;
        }
        catch (Exception ex)
        {
            ShowError("Не удалось завершить сессию. Программа останется открытой, чтобы журнал не потерялся.", ex);
            e.Cancel = true;
        }
    }

}
