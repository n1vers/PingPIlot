using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingPilot.Services;

public readonly record struct PingResult(
    bool Ok,
    long? LatencyMs,
    string Status,
    string ResolvedIp = "");

public static class PingService
{
    public static async Task<PingResult> PingAsync(
        string host,
        int timeoutMs = 1000)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new PingResult(false, null, "No address");

        string normalizedHost = host.Trim();
        string resolvedIp = string.Empty;

        try
        {
            IPAddress targetAddress;

            if (IPAddress.TryParse(normalizedHost, out IPAddress? parsedAddress))
            {
                // 0.0.0.0 / :: — это неопределённые адреса, а не адрес компьютера.
                if (!IsValidPingTarget(parsedAddress))
                    return new PingResult(false, null, "Invalid IP address");

                targetAddress = parsedAddress;
            }
            else
            {
                // DNS lookup is part of the ping operation as well. Without a
                // timeout a broken/unreachable resolver can keep the whole
                // refresh cycle alive indefinitely, causing every subsequent
                // DispatcherTimer tick to be skipped.
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(normalizedHost)
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

                // Не используем первый попавшийся адрес без проверки: DNS иногда
                // может вернуть 0.0.0.0 / ::, на которые Ping.SendPingAsync бросает исключение.
                targetAddress = addresses.FirstOrDefault(a =>
                        a.AddressFamily == AddressFamily.InterNetwork && IsValidPingTarget(a))
                    ?? addresses.FirstOrDefault(IsValidPingTarget)
                    ?? throw new SocketException((int)SocketError.HostNotFound);
            }

            // Сюда попадают только адреса, которые разрешено использовать как цель ping.
            resolvedIp = targetAddress.ToString();

            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(targetAddress, timeoutMs);

            // Не заменяем корректный адрес ответом, если он по какой-либо причине
            // оказался неопределённым.
            if (reply.Address is { } replyAddress && IsValidPingTarget(replyAddress))
                resolvedIp = replyAddress.ToString();

            return reply.Status switch
            {
                IPStatus.Success => new PingResult(true, reply.RoundtripTime, "Online", resolvedIp),
                IPStatus.TimedOut => new PingResult(false, null, "Timeout", resolvedIp),
                _ => new PingResult(false, null, "Unreachable", resolvedIp)
            };
        }
        catch (Exception ex) when (IsTimeoutException(ex))
        {
            // Тайм-аут — нормальный результат мониторинга, а не фатальная ошибка.
            // Возвращаем его вызывающему коду, чтобы плитка обновилась и следующий
            // цикл ping продолжился. Последний корректный IP сохраняем.
            return new PingResult(false, null, "Timeout", resolvedIp);
        }
        catch (SocketException)
        {
            // Если адрес не был определён, это ошибка DNS/имени. Если адрес уже
            // известен, сохраняем его и показываем обычную ошибку проверки.
            return string.IsNullOrWhiteSpace(resolvedIp)
                ? new PingResult(false, null, "DNS lookup failed", string.Empty)
                : new PingResult(false, null, "Unreachable", resolvedIp);
        }
        catch (PingException)
        {
            return new PingResult(false, null, "Unreachable", resolvedIp);
        }
        catch (ArgumentException)
        {
            // Дополнительная страховка от некорректного адреса/параметров ping.
            return new PingResult(false, null, "Invalid IP address", string.Empty);
        }
        catch (Exception)
        {
            return new PingResult(false, null, "Check failed", resolvedIp);
        }
    }


    private static bool IsTimeoutException(Exception exception)
    {
        // Ping APIs may report timeout either as a direct exception or wrapped
        // inside PingException. Check the complete inner-exception chain.
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;

            if (current is SocketException socketException &&
                socketException.SocketErrorCode == SocketError.TimedOut)
                return true;

            // Windows ERROR_TIMEOUT (1460) and Winsock WSAETIMEDOUT (10060).
            if (current is Win32Exception win32Exception &&
                (win32Exception.NativeErrorCode == 1460 ||
                 win32Exception.NativeErrorCode == 10060))
                return true;

            string message = current.Message;
            if (message.Contains("превышен интервал ожидания", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("время ожидания истекло", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsValidPingTarget(IPAddress address)
    {
        if (IPAddress.Any.Equals(address) ||
            IPAddress.IPv6Any.Equals(address) ||
            IPAddress.Broadcast.Equals(address) ||
            IPAddress.None.Equals(address) ||
            IPAddress.IPv6None.Equals(address) ||
            address.IsIPv6Multicast)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();

        // IPv4 multicast (224.0.0.0/4) — это группа, а не отдельный компьютер.
        if (address.AddressFamily == AddressFamily.InterNetwork &&
            bytes[0] >= 224 && bytes[0] <= 239)
        {
            return false;
        }

        return true;
    }
}
