using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PingPilot.Models;
using PingPilot.Services;

namespace PingPilot;

// Настройки источника, периодическое сканирование и фильтрация результатов.
public partial class MainWindow
{
    private const int MinAutoScanIntervalSeconds = 10;
    private const int MaxAutoScanIntervalSeconds = 3600;

    private readonly NetworkDiscoveryService _discovery = new();
    private readonly NetworkScannerService _scanner = new();
    private readonly DispatcherTimer _autoScanTimer;
    private CancellationTokenSource? _scanCts;
    private List<ScanResult> _allScanResults = new();
    private bool _loadingScannerSettings;
    private bool _isScanning;
    private bool _currentScanIsAutomatic;

    private bool IsRangeMode => string.Equals(
        (ScanTargetModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
        "IpRange", StringComparison.OrdinalIgnoreCase);

    private void InitializeScannerSettings()
    {
        _loadingScannerSettings = true;
        ScannerSettings settings = _data.ScanSettings ??= new ScannerSettings();
        ScanTargetModeCombo.SelectedIndex =
            string.Equals(settings.TargetMode, "IpRange", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        RangeStartBox.Text = settings.RangeStart ?? string.Empty;
        RangeEndBox.Text = settings.RangeEnd ?? string.Empty;
        AutoScanIntervalBox.Text = settings.AutoScanIntervalSeconds.ToString();
        AutoScanCheckBox.IsChecked = settings.AutoScanEnabled;
        MaxLatencyCombo.SelectedIndex = 0;
        DnsOnlyCheckBox.IsChecked = false;
        FilterTextBox.Text = string.Empty;
        _loadingScannerSettings = false;

        UpdateTargetModeUi();
        if (IsRangeMode)
            FillRangeFromSelectedNetworkIfEmpty();
        ApplyResultFilter();

        if (!settings.AutoScanEnabled)
        {
            AutoScanStatusText.Text = "Автосканирование выключено";
            return;
        }

        if (!TryGetAutoScanInterval(out int interval, out _) ||
            !TryResolveScanTarget(out _, out _, out _, out _, out _))
        {
            _loadingScannerSettings = true;
            AutoScanCheckBox.IsChecked = false;
            _loadingScannerSettings = false;
            settings.AutoScanEnabled = false;
            SaveData();
            AutoScanStatusText.Text = "Автосканирование выключено: проверьте настройки.";
            return;
        }

        StartAutoScan(interval, runImmediately: true);
    }

    private void LoadNetworks()
    {
        try
        {
            List<NetworkInfo> networks = _discovery.GetLocalNetworks();
            string? previousNetwork = (NetworkCombo.SelectedItem as NetworkInfo)?.ToString();
            NetworkCombo.ItemsSource = networks;
            NetworkCombo.SelectedItem = networks.FirstOrDefault(n => n.ToString() == previousNetwork)
                ?? networks.FirstOrDefault();

            ScanStatusText.Text = networks.Count == 0
                ? "Локальные сети не найдены. Можно переключиться на режим «Диапазон IP»."
                : "Выберите локальную сеть или задайте диапазон IP-адресов.";
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = $"Не удалось определить локальные сети: {ex.Message}";
        }

        UpdateTargetModeUi();
    }

    private void UpdateTargetModeUi()
    {
        bool rangeMode = IsRangeMode;
        RangeTargetPanel.Visibility = rangeMode ? Visibility.Visible : Visibility.Collapsed;
        NetworkCombo.Visibility = rangeMode ? Visibility.Collapsed : Visibility.Visible;
        NetworkCombo.IsEnabled = !_isScanning && !rangeMode;
        RangeStartBox.IsEnabled = !_isScanning;
        RangeEndBox.IsEnabled = !_isScanning;
    }

    private void SetScanning(bool scanning)
    {
        _isScanning = scanning;
        ScanTargetModeCombo.IsEnabled = !scanning;
        NetworkCombo.IsEnabled = !scanning && !IsRangeMode;
        RangeStartBox.IsEnabled = !scanning;
        RangeEndBox.IsEnabled = !scanning;
        RefreshNetworksButton.IsEnabled = !scanning;
        ScanButton.IsEnabled = !scanning;
        StopScanButton.IsEnabled = scanning;
    }

    private void SaveScannerSettingsFromUi(bool? autoScanEnabled = null)
    {
        _data.ScanSettings ??= new ScannerSettings();
        _data.ScanSettings.TargetMode = IsRangeMode ? "IpRange" : "LocalNetwork";
        _data.ScanSettings.RangeStart = RangeStartBox.Text.Trim();
        _data.ScanSettings.RangeEnd = RangeEndBox.Text.Trim();

        if (int.TryParse(AutoScanIntervalBox.Text.Trim(), out int interval) &&
            interval >= MinAutoScanIntervalSeconds && interval <= MaxAutoScanIntervalSeconds)
        {
            _data.ScanSettings.AutoScanIntervalSeconds = interval;
        }

        if (autoScanEnabled.HasValue)
            _data.ScanSettings.AutoScanEnabled = autoScanEnabled.Value;
        SaveData();
    }

    private bool TryGetAutoScanInterval(out int interval, out string error)
    {
        if (!int.TryParse(AutoScanIntervalBox.Text.Trim(), out interval) ||
            interval < MinAutoScanIntervalSeconds || interval > MaxAutoScanIntervalSeconds)
        {
            error = $"Интервал автосканирования должен быть от {MinAutoScanIntervalSeconds} до {MaxAutoScanIntervalSeconds} секунд.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private bool TryResolveScanTarget(
        out NetworkInfo? network,
        out string? startIp,
        out string? endIp,
        out int totalAddresses,
        out string error)
    {
        network = null;
        startIp = null;
        endIp = null;
        totalAddresses = 0;

        if (!IsRangeMode)
        {
            if (NetworkCombo.SelectedItem is not NetworkInfo selectedNetwork)
            {
                error = "Локальные сети не найдены. Переключитесь на «Диапазон IP» и введите адреса вручную.";
                return false;
            }

            network = selectedNetwork;
            totalAddresses = selectedNetwork.HostCount;
            error = string.Empty;
            return true;
        }

        string startText = RangeStartBox.Text.Trim();
        string endText = RangeEndBox.Text.Trim();
        if (!IPAddress.TryParse(startText, out IPAddress? start) ||
            start.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Введите корректный начальный IPv4-адрес, например 192.168.1.1.";
            return false;
        }

        if (!IPAddress.TryParse(endText, out IPAddress? end) ||
            end.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Введите корректный конечный IPv4-адрес, например 192.168.1.254.";
            return false;
        }

        uint first = IpUtil.ToUInt(start);
        uint last = IpUtil.ToUInt(end);
        if (last < first)
        {
            error = "Конечный адрес не может быть меньше начального.";
            return false;
        }

        long count = (long)last - first + 1;
        if (count > NetworkScannerService.MaxRangeAddresses)
        {
            error = $"Выбрано адресов: {count:N0}. За одно сканирование можно проверить не более {NetworkScannerService.MaxRangeAddresses:N0}.";
            return false;
        }

        startIp = start.ToString();
        endIp = end.ToString();
        totalAddresses = (int)count;
        error = string.Empty;
        return true;
    }

    private void TargetModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingScannerSettings)
            return;

        UpdateTargetModeUi();
        if (IsRangeMode)
            FillRangeFromSelectedNetworkIfEmpty();
        SaveScannerSettingsFromUi();
    }

    private void FillRangeFromSelectedNetworkIfEmpty()
    {
        if (NetworkCombo.SelectedItem is not NetworkInfo network ||
            (!string.IsNullOrWhiteSpace(RangeStartBox.Text) && !string.IsNullOrWhiteSpace(RangeEndBox.Text)))
            return;

        try
        {
            uint networkAddress = IpUtil.ToUInt(IPAddress.Parse(network.NetworkAddress));
            uint firstHost = networkAddress + 1;
            uint lastHost = networkAddress + (uint)network.HostCount;

            if (string.IsNullOrWhiteSpace(RangeStartBox.Text))
                RangeStartBox.Text = IpUtil.FromUInt(firstHost).ToString();
            if (string.IsNullOrWhiteSpace(RangeEndBox.Text))
                RangeEndBox.Text = IpUtil.FromUInt(lastHost).ToString();
        }
        catch (Exception)
        {
            // При некорректных данных интерфейса оставляем поля для ручного ввода.
        }
    }

    private void ScanTargetInputs_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loadingScannerSettings)
            SaveScannerSettingsFromUi();
    }

    private void AutoScanIntervalBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingScannerSettings)
            return;

        if (!TryGetAutoScanInterval(out int interval, out string error))
        {
            AutoScanStatusText.Text = error;
            return;
        }

        SaveScannerSettingsFromUi();
        if (AutoScanCheckBox.IsChecked == true)
        {
            _autoScanTimer.Interval = TimeSpan.FromSeconds(interval);
            _autoScanTimer.Stop();
            _autoScanTimer.Start();
            AutoScanStatusText.Text = $"Автосканирование · каждые {interval} с";
        }
    }

    private void AutoScanCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingScannerSettings)
            return;

        if (AutoScanCheckBox.IsChecked == true)
        {
            if (!TryGetAutoScanInterval(out int interval, out string intervalError))
            {
                SetAutoScanCheckbox(false);
                AutoScanStatusText.Text = intervalError;
                ShowWarning(intervalError);
                return;
            }

            if (!TryResolveScanTarget(out _, out _, out _, out _, out string targetError))
            {
                SetAutoScanCheckbox(false);
                AutoScanStatusText.Text = targetError;
                ShowWarning(targetError);
                return;
            }

            SaveScannerSettingsFromUi(autoScanEnabled: true);
            StartAutoScan(interval, runImmediately: true);
        }
        else
        {
            _autoScanTimer.Stop();
            SaveScannerSettingsFromUi(autoScanEnabled: false);
            AutoScanStatusText.Text = "Автосканирование выключено";

            // Остановка автосканирования также прерывает текущий автоматический проход.
            if (_currentScanIsAutomatic)
                _scanCts?.Cancel();
        }
    }

    private void SetAutoScanCheckbox(bool value)
    {
        _loadingScannerSettings = true;
        AutoScanCheckBox.IsChecked = value;
        _loadingScannerSettings = false;
        _data.ScanSettings.AutoScanEnabled = value;
        SaveData();
    }

    private void StartAutoScan(int intervalSeconds, bool runImmediately)
    {
        _autoScanTimer.Interval = TimeSpan.FromSeconds(intervalSeconds);
        _autoScanTimer.Start();
        AutoScanStatusText.Text = $"Автосканирование · каждые {intervalSeconds} с";

        if (runImmediately && !_isScanning)
            _ = RunScanAsync(isAutomatic: true);
    }

    private async void AutoScanTimer_Tick(object? sender, EventArgs e)
    {
        if (AutoScanCheckBox.IsChecked != true || _isScanning)
            return;

        await RunScanAsync(isAutomatic: true);
    }

    private void RefreshNetworksButton_Click(object sender, RoutedEventArgs e) => LoadNetworks();

    private async void ScanButton_Click(object sender, RoutedEventArgs e) =>
        await RunScanAsync(isAutomatic: false);

    private async Task RunScanAsync(bool isAutomatic)
    {
        if (_isScanning)
        {
            if (!isAutomatic)
                ScanStatusText.Text = "Сканирование уже выполняется.";
            return;
        }

        if (!TryResolveScanTarget(
                out NetworkInfo? network,
                out string? startIp,
                out string? endIp,
                out int totalAddresses,
                out string targetError))
        {
            ScanStatusText.Text = targetError;
            if (!isAutomatic)
                ShowWarning(targetError);
            return;
        }

        SaveScannerSettingsFromUi();

        var currentCts = new CancellationTokenSource();
        _scanCts = currentCts;
        _currentScanIsAutomatic = isAutomatic;
        SetScanning(true);
        ScanProgress.Maximum = Math.Max(1, totalAddresses);
        ScanProgress.Value = 0;
        ScanStatusText.Text = isAutomatic ? "Автосканирование: проверка адресов..." : "Сканирование...";

        var progress = new Progress<int>(checkedCount =>
        {
            // Не обновляем прогресс завершившегося или отменённого прохода.
            if (!ReferenceEquals(_scanCts, currentCts) || currentCts.IsCancellationRequested)
                return;

            ScanProgress.Value = checkedCount;
            string prefix = isAutomatic ? "Автосканирование" : "Сканирование";
            ScanStatusText.Text = $"{prefix}: проверено {checkedCount:N0} из {totalAddresses:N0}";
        });

        try
        {
            List<ScanResult> found = network != null
                ? await _scanner.ScanAsync(network, progress, currentCts.Token)
                : await _scanner.ScanRangeAsync(startIp!, endIp!, progress, currentCts.Token);

            if (currentCts.IsCancellationRequested)
                return;

            _allScanResults = found;
            ApplyResultFilter();
            ScanProgress.Value = ScanProgress.Maximum;
            ScanStatusText.Text = isAutomatic
                ? $"Автосканирование завершено: найдено {found.Count} устройств · {DateTime.Now:HH:mm:ss}"
                : $"Сканирование завершено. Найдено устройств: {found.Count}";
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = isAutomatic
                ? "Текущий автоматический проход остановлен."
                : "Сканирование остановлено.";
        }
        catch (Exception ex)
        {
            ScanStatusText.Text = $"Сканирование не удалось: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_scanCts, currentCts))
                _scanCts = null;

            _currentScanIsAutomatic = false;
            SetScanning(false);
            currentCts.Dispose();
        }
    }

    private void StopScanButton_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyResultFilter();

    private void ResultFilter_Changed(object sender, RoutedEventArgs e) => ApplyResultFilter();

    private void MaxLatencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyResultFilter();
    }

    private void ApplyResultFilter()
    {
        if (ResultsGrid == null || FilterTextBox == null || DnsOnlyCheckBox == null || MaxLatencyCombo == null)
            return;

        string search = FilterTextBox.Text.Trim();
        bool dnsOnly = DnsOnlyCheckBox.IsChecked == true;
        int maxLatency = int.TryParse(MaxLatencyCombo.SelectedValue?.ToString(), out int parsed)
            ? parsed
            : 0;

        List<ScanResult> visible = _allScanResults.Where(result =>
        {
            bool matchesText = search.Length == 0 ||
                result.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                result.IpAddress.Contains(search, StringComparison.OrdinalIgnoreCase);
            bool hasDnsName = !string.Equals(result.Name, result.IpAddress, StringComparison.OrdinalIgnoreCase);
            bool matchesDns = !dnsOnly || hasDnsName;
            bool matchesLatency = maxLatency <= 0 || result.LatencyMs <= maxLatency;
            return matchesText && matchesDns && matchesLatency;
        }).ToList();

        ResultsGrid.ItemsSource = visible;
        FilterSummaryText.Text = $"Показано {visible.Count} из {_allScanResults.Count} найденных устройств";
    }

    private void AddFoundButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsGrid.SelectedItems.OfType<ScanResult>().ToList();
        if (selected.Count == 0)
        {
            ScanStatusText.Text = "Выберите одно или несколько устройств в списке (Ctrl или Shift — для нескольких).";
            return;
        }

        if (!EnsureGroup())
            return;

        ComputerGroup? group = Dialogs.PickGroup(this, _data.Groups, "Добавить в группу");
        if (group == null)
            return;

        int added = AddComputers(selected.Select(r => new SavedComputer
        {
            Name = r.Name,
            Host = r.IpAddress,
            GroupId = group.Id
        }));

        ScanStatusText.Text = $"В группу «{group.Name}» добавлено: {added} из {selected.Count}.";
        LoadGroups(group.Id);
        MainTabs.SelectedIndex = 0;
    }
}
