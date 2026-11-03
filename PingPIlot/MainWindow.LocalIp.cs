using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using PingPilot.Services;

namespace PingPilot;

// Вывод IP-адресов этого компьютера в верхней части дашборда.
public partial class MainWindow
{
    private readonly NetworkDiscoveryService _localIpDiscovery = new();
    private NetworkAddressChangedEventHandler? _networkChangedHandler;

    private void InitializeLocalIps()
    {
        RefreshLocalIps();

        // Адреса меняются при подключении к Wi-Fi / VPN / смене сети — обновляем сами.
        _networkChangedHandler = (_, _) => Dispatcher.BeginInvoke(new Action(RefreshLocalIps));
        NetworkChange.NetworkAddressChanged += _networkChangedHandler;
        Closed += (_, _) =>
        {
            if (_networkChangedHandler != null)
                NetworkChange.NetworkAddressChanged -= _networkChangedHandler;
        };
    }

    private void RefreshLocalIps()
    {
        try
        {
            var addresses = _localIpDiscovery.GetLocalAddresses();
            LocalIpList.ItemsSource = addresses.Count > 0
                ? addresses
                : new List<LocalAddressInfo> { new("", "нет подключения") };

            var gateways = _localIpDiscovery.GetGateways();
            GatewayList.ItemsSource = gateways.Count > 0
                ? gateways
                : new List<LocalAddressInfo> { new("", "нет шлюза") };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Не удалось получить локальные IP: {ex.Message}");
        }
    }

    private void LocalIpChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string address } || address.Contains(' '))
            return;

        try
        {
            Clipboard.SetText(address);

            if (sender is FrameworkElement element)
            {
                const string copyHint = "Нажмите, чтобы скопировать IP-адрес";
                element.ToolTip = "Скопировано в буфер обмена";

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    element.ToolTip = copyHint;
                    timer.Stop();
                };
                timer.Start();
            }
        }
        catch { /* буфер обмена может быть занят другим приложением */ }
    }
}
