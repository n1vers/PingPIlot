using System.Buffers.Binary;
using System.Net;

namespace PingPilot.Services;

public static class IpUtil
{
    public static uint ToUInt(IPAddress address) =>
        BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    public static IPAddress FromUInt(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
