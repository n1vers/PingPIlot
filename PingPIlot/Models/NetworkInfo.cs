namespace PingPilot.Models;

public class NetworkInfo
{
    public string InterfaceName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string NetworkAddress { get; set; } = "";
    public int PrefixLength { get; set; }

    // Количество адресов для проверки (без адреса сети и broadcast)
    public int HostCount => (int)((1L << (32 - PrefixLength)) - 2);

    public override string ToString() =>
        $"{InterfaceName}: {NetworkAddress}/{PrefixLength}  (мой IP {IpAddress})";
}
