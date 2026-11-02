using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using PingPilot.Models;

namespace PingPilot.Services;

public class NetworkDiscoveryService
{
    // Сканер работает только с сетями от /16 до /30.
    public const int MinPrefix = 16;
    public const int MaxPrefix = 30;

    public List<NetworkInfo> GetLocalNetworks()
    {
        var networks = new List<NetworkInfo>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask == null)
                    continue;

                uint mask = IpUtil.ToUInt(unicast.IPv4Mask);
                int prefix = BitOperations.PopCount(mask);

                if (prefix < MinPrefix || prefix > MaxPrefix)
                    continue;

                uint network = IpUtil.ToUInt(unicast.Address) & mask;

                networks.Add(new NetworkInfo
                {
                    InterfaceName = nic.Name,
                    IpAddress = unicast.Address.ToString(),
                    NetworkAddress = IpUtil.FromUInt(network).ToString(),
                    PrefixLength = prefix
                });
            }
        }

        return networks
            .OrderBy(n => n.InterfaceName)
            .ThenBy(n => n.IpAddress)
            .ToList();
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetBestInterface(uint destinationAddress, out uint interfaceIndex);

    // Шлюзы по умолчанию (IPv4) активных подключений. Шлюз, через который реально уходит
    // трафик в интернет, идёт первым; остальные (например, VPN) — после него.
    public List<LocalAddressInfo> GetGateways()
    {
        int bestIndex = -1;
        try
        {
            // 8.8.8.8 как «любой внешний адрес»: Windows сама выбирает интерфейс по таблице маршрутов.
            uint probe = BitConverter.ToUInt32(new byte[] { 8, 8, 8, 8 }, 0);
            if (GetBestInterface(probe, out uint index) == 0)
                bestIndex = (int)index;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Без API Windows просто не отдаём предпочтение ни одному шлюзу.
        }

        var result = new List<(bool Best, LocalAddressInfo Info)>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            try
            {
                IPInterfaceProperties properties = nic.GetIPProperties();
                int nicIndex = properties.GetIPv4Properties()?.Index ?? -2;

                foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily != AddressFamily.InterNetwork ||
                        Equals(gateway.Address, IPAddress.Any))
                        continue;

                    result.Add((nicIndex == bestIndex, new LocalAddressInfo(nic.Name, gateway.Address.ToString())));
                }
            }
            catch (NetworkInformationException)
            {
                // Один виртуальный адаптер не должен скрывать шлюзы остальных интерфейсов.
            }
        }

        return result
            .OrderByDescending(r => r.Best)
            .ThenBy(r => r.Info.Name)
            .Select(r => r.Info)
            .ToList();
    }

    // Все IPv4-адреса этого компьютера (для вывода на дашборде), без loopback и APIPA.
    public List<LocalAddressInfo> GetLocalAddresses()
    {
        var result = new List<LocalAddressInfo>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                byte[] b = unicast.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                    continue;

                result.Add(new LocalAddressInfo(nic.Name, unicast.Address.ToString()));
            }
        }

        return result
            .OrderBy(a => a.Name)
            .ThenBy(a => a.Address)
            .ToList();
    }
}

public record LocalAddressInfo(string Name, string Address);
