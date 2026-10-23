using System.Net;
using System.Net.Sockets;
using PingPilot.Models;

namespace PingPilot.Services;

public class NetworkScannerService
{
    private const int Parallelism = 64;
    private const int PingTimeoutMs = 700;
    public const int MaxRangeAddresses = 65_534;

    public Task<List<ScanResult>> ScanAsync(
        NetworkInfo network,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        uint first = IpUtil.ToUInt(IPAddress.Parse(network.NetworkAddress)) + 1;
        return ScanAddressesAsync(first, network.HostCount, progress, cancellationToken);
    }

    /// <summary>Сканирует все IPv4-адреса включительно от startAddress до endAddress.</summary>
    public Task<List<ScanResult>> ScanRangeAsync(
        string startAddress,
        string endAddress,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(startAddress, out IPAddress? start) ||
            start.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The start address must be a valid IPv4 address.", nameof(startAddress));

        if (!IPAddress.TryParse(endAddress, out IPAddress? end) ||
            end.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The end address must be a valid IPv4 address.", nameof(endAddress));

        uint first = IpUtil.ToUInt(start);
        uint last = IpUtil.ToUInt(end);

        if (last < first)
            throw new ArgumentException("The end address must not be lower than the start address.");

        long countLong = (long)last - first + 1;
        if (countLong > MaxRangeAddresses)
            throw new ArgumentException(
                $"The range contains {countLong:N0} addresses. The maximum per scan is {MaxRangeAddresses:N0}.");

        return ScanAddressesAsync(first, (int)countLong, progress, cancellationToken);
    }

    private static async Task<List<ScanResult>> ScanAddressesAsync(
        uint firstAddress,
        int total,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (total <= 0)
            return new List<ScanResult>();

        var found = new List<ScanResult>();
        int done = 0;
        IEnumerable<uint> addresses = Enumerable.Range(0, total)
            .Select(i => firstAddress + (uint)i);

        await Parallel.ForEachAsync(
            addresses,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Parallelism,
                CancellationToken = cancellationToken
            },
            async (address, token) =>
            {
                token.ThrowIfCancellationRequested();
                ScanResult? result = await ProbeAsync(IpUtil.FromUInt(address).ToString());

                if (result != null)
                {
                    lock (found)
                        found.Add(result);
                }

                int checkedCount = Interlocked.Increment(ref done);
                // Не засыпаем Dispatcher событиями прогресса.
                if (checkedCount % 16 == 0 || checkedCount == total)
                    progress?.Report(checkedCount);
            });

        return found
            .OrderBy(r => IpUtil.ToUInt(IPAddress.Parse(r.IpAddress)))
            .ToList();
    }

    private static async Task<ScanResult?> ProbeAsync(string ip)
    {
        PingResult ping = await PingService.PingAsync(ip, PingTimeoutMs);

        if (!ping.Ok)
            return null;

        string name = ip;
        try
        {
            IPHostEntry entry = await Dns.GetHostEntryAsync(ip);
            if (!string.IsNullOrWhiteSpace(entry.HostName))
                name = entry.HostName;
        }
        catch (Exception)
        {
            // Если обратная DNS-запись отсутствует, используем IP как имя.
        }

        return new ScanResult
        {
            Name = name,
            IpAddress = ip,
            LatencyMs = ping.LatencyMs ?? 0
        };
    }
}
