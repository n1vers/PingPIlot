
using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PingPilot.Services;

public class NetworkDetailsService
{
    public string GetNetworkInterfaceName(IPAddress targetAddress)
    {
        if (targetAddress.AddressFamily != AddressFamily.InterNetwork)
            return "Unknown";

        byte[] bytes = targetAddress.GetAddressBytes();
        uint destination = BitConverter.ToUInt32(bytes, 0);

        if (GetBestInterface(destination, out uint index) != 0)
            return "Unknown";

        var adapter = NetworkInterface
            .GetAllNetworkInterfaces()
            .FirstOrDefault(n =>
                n.GetIPProperties().GetIPv4Properties()?.Index == index);

        return adapter == null
            ? "Unknown"
            : $"{adapter.Name} ({adapter.NetworkInterfaceType})";
    }

    public string GetMacAddress(IPAddress targetAddress)
    {
        if (targetAddress.AddressFamily != AddressFamily.InterNetwork)
            return "Unknown";

        bool localSubnet = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(u =>
                u.Address.AddressFamily == AddressFamily.InterNetwork &&
                u.IPv4Mask != null)
            .Any(u => SameSubnet(targetAddress, u.Address, u.IPv4Mask!));

        if (!localSubnet)
            return "Remote network";

        byte[] mac = new byte[6];
        uint length = (uint)mac.Length;

        uint destination = BitConverter.ToUInt32(
            targetAddress.GetAddressBytes(), 0);

        int result = SendARP(destination, 0, mac, ref length);

        if (result != 0 || length == 0)
            return "Unknown";

        return string.Join(
            ":",
            mac.Take((int)length).Select(b => b.ToString("X2")));
    }

    private static bool SameSubnet(
        IPAddress target,
        IPAddress local,
        IPAddress mask)
    {
        byte[] t = target.GetAddressBytes();
        byte[] l = local.GetAddressBytes();
        byte[] m = mask.GetAddressBytes();

        for (int i = 0; i < 4; i++)
        {
            if ((t[i] & m[i]) != (l[i] & m[i]))
                return false;
        }

        return true;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetBestInterface(
        uint destinationAddress,
        out uint interfaceIndex);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(
        uint destinationAddress,
        uint sourceAddress,
        [Out] byte[] physicalAddress,
        ref uint physicalAddressLength);
}
