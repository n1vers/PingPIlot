using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        try { Clipboard.SetText(address); }
        catch { /* буфер обмена может быть занят другим приложением */ }
    }
}
