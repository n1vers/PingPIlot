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
