using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingPilot.Services;

public readonly record struct PingResult(
    bool Ok,
    long? LatencyMs,
    string Status,
    string ResolvedIp = "");   // IP, который реально проверялся (не адрес того, кто прислал ответ)

public static class PingService
{
    // DNS: сколько ждём ответ резолвера и как долго доверяем найденному адресу.
    private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromSeconds(30);

    // Если проверка по закэшированному адресу не удалась, имя пересчитывается заново,
    // но не чаще этого интервала — чтобы не заваливать DNS запросами каждую секунду.
    private static readonly TimeSpan MinReResolveInterval = TimeSpan.FromSeconds(5);

    // Одно имя может вернуть несколько адресов (старая запись, VPN, IPv6). Пробуем несколько.
    private const int MaxCandidates = 4;
    private const int RdpPort = 3389;

    private sealed class DnsEntry
    {
        public DnsEntry(IPAddress[] addresses, DateTime resolvedAt, IPAddress? lastGood)
        {
            Addresses = addresses;
            ResolvedAt = resolvedAt;
            LastGood = lastGood;
        }

        public IPAddress[] Addresses { get; }
        // Это время последней попытки DNS. При временной ошибке DNS оно тоже
        // обновляется, чтобы не запускать новый резолв на каждый пакет.
        public DateTime ResolvedAt { get; private set; }

        /// <summary>Адрес, который в последний раз ответил на ping. Проверяется первым.</summary>
        public volatile IPAddress? LastGood;

        public void MarkResolveAttempt() => ResolvedAt = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<string, DnsEntry> DnsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, DateTime> DnsFailures =
        new(StringComparer.OrdinalIgnoreCase);

    // Если одновременно (10 пакетов в секунду на компьютер) истёк срок DNS-кэша, имя
    // спрашивается один раз, а все остальные пакеты ждут тот же запрос, а не шлют 10 своих.
    private static readonly ConcurrentDictionary<string, Task<IPAddress[]>> ResolveInFlight =
        new(StringComparer.OrdinalIgnoreCase);

    // Объекты Ping переиспользуются: каждый держит собственный системный ICMP-дескриптор,
    // и создавать/закрывать его на каждый из десятков пакетов в секунду дорого.
    // Один экземпляр одновременно обслуживает только один запрос, поэтому это именно пул.
    private static readonly ConcurrentQueue<Ping> PingPool = new();
    private const int MaxPooledPings = 64;

    /// <summary>Забывает все найденные адреса: следующая проверка заново спросит DNS.</summary>
    public static void ClearDnsCache()
    {
        DnsCache.Clear();
        DnsFailures.Clear();
    }

    /// <summary>
    /// Проверяет доступность службы удалённого рабочего стола по TCP-порту 3389.
    /// Ошибки DNS, тайм-ауты и закрытый порт считаются недоступностью и не прерывают мониторинг.
    /// </summary>
    public static async Task<bool> CheckRdpPortAsync(string host, int timeoutMs = 700)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        string normalizedHost = host.Trim();
        int timeout = Math.Clamp(timeoutMs, 100, 5000);

        try
        {
            IPAddress[] addresses;

            if (IPAddress.TryParse(normalizedHost, out IPAddress? literal))
            {
                if (!IsValidPingTarget(literal))
                    return false;

                addresses = new[] { literal };
            }
            else
            {
                // Используем ту же DNS-валидацию и ограничение ожидания, что и для ping.
                addresses = await ResolveAsync(normalizedHost, timeout);
            }

            foreach (IPAddress address in addresses.Take(MaxCandidates))
            {
                try
                {
                    using var client = new TcpClient(address.AddressFamily);
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
                    await client.ConnectAsync(address, RdpPort, cts.Token);
                    return true;
                }
                catch (Exception ex) when (
                    ex is SocketException or OperationCanceledException or TimeoutException or
                    ArgumentException or InvalidOperationException or ObjectDisposedException or IOException)
                {
                    // Один адрес может быть недоступен, но другой адрес из DNS ещё может работать.
                }
            }
        }
        catch (Exception ex) when (
            ex is SocketException or OperationCanceledException or TimeoutException or ArgumentException)
        {
            // DNS-ошибка или тайм-аут — RDP недоступен; приложение должно продолжить работу.
        }

        return false;
    }

    /// <param name="deepCheck">
    /// Усиленная проверка («Проверить все»): DNS спрашивается заново, на каждый адрес
    /// делается до двух попыток — примерно как это делает ping.exe в Windows.
    /// </param>
    public static async Task<PingResult> PingAsync(
        string host,
        int timeoutMs = 800,
        bool deepCheck = false)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new PingResult(false, null, "No address");

        string normalizedHost = host.Trim();
        int attempts = deepCheck ? 2 : 1;

        try
        {
            // IP, введённый руками, пингуется как есть: никакого кэша и подмены адреса.
            if (IPAddress.TryParse(normalizedHost, out IPAddress? literal))
            {
                // 0.0.0.0 / :: — это неопределённые адреса, а не адрес компьютера.
                return IsValidPingTarget(literal)
                    ? await PingWithRetryAsync(literal, timeoutMs, attempts)
                    : new PingResult(false, null, "Invalid IP address");
            }

            return await PingHostNameAsync(normalizedHost, timeoutMs, deepCheck, attempts);
        }
        catch (SocketException)
        {
            // Имя не нашлось и в кэше ничего нет.
            return new PingResult(false, null, "DNS lookup failed");
        }
        catch (TimeoutException)
        {
            return new PingResult(false, null, "DNS lookup failed");
        }
        catch (ArgumentException)
        {
            return new PingResult(false, null, "Invalid IP address");
        }
        catch (Exception)
        {
            return new PingResult(false, null, "Check failed");
        }
    }

    private static async Task<PingResult> PingHostNameAsync(
        string host, int timeoutMs, bool deepCheck, int attempts)
    {
        DnsEntry entry = await GetEntryAsync(host, deepCheck, timeoutMs);
        // Обычная проверка по таймеру — один быстрый ping, как раньше, чтобы цикл не растягивался.
        // Остальные адреса из DNS перебираются только при полной проверке.
        return await PingCandidatesAsync(entry, timeoutMs, attempts, tryAll: deepCheck);
    }

    private static async Task<DnsEntry> GetEntryAsync(string host, bool force, int timeoutMs)
    {
        DnsCache.TryGetValue(host, out DnsEntry? cached);

        // Имя только что не нашлось — не спрашиваем DNS на каждой проверке,
        // иначе медленный резолвер тормозит обновление пинга.
        if (!force && cached == null &&
            DnsFailures.TryGetValue(host, out DateTime failedAt) &&
            DateTime.UtcNow - failedAt < MinReResolveInterval)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        if (!force && cached != null && DateTime.UtcNow - cached.ResolvedAt < DnsCacheTtl)
            return cached;

        try
        {
            IPAddress[] addresses = await ResolveSharedAsync(host, timeoutMs);
            var entry = new DnsEntry(addresses, DateTime.UtcNow, cached?.LastGood);
            DnsCache[host] = entry;
            DnsFailures.TryRemove(host, out _);
            return entry;
        }
        catch (Exception) when (cached == null)
        {
            DnsFailures[host] = DateTime.UtcNow;
            throw;
        }
        catch (Exception) when (cached != null)
        {
            // Резолвер временно недоступен: лучше проверить по последнему известному адресу,
            // чем сразу показывать «Ошибка DNS». Сохраняем время попытки, чтобы
            // ошибочный DNS не запускался снова на каждом из частых ping-пакетов.
            cached.MarkResolveAttempt();
            return cached;
        }
    }

    /// <summary>Один DNS-запрос на имя: параллельные вызовы получают результат того же запроса.</summary>
    private static Task<IPAddress[]> ResolveSharedAsync(string host, int timeoutMs)
    {
        var completion = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IPAddress[]> stored = ResolveInFlight.GetOrAdd(host, completion.Task);

        // Такой запрос уже выполняется — просто ждём его результат.
        if (!ReferenceEquals(stored, completion.Task))
            return stored;

        _ = RunResolveAsync(host, timeoutMs, completion);
        return completion.Task;
    }

    private static async Task RunResolveAsync(
        string host, int timeoutMs, TaskCompletionSource<IPAddress[]> completion)
    {
        try
        {
            completion.TrySetResult(await ResolveAsync(host, timeoutMs).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            ResolveInFlight.TryRemove(host, out _);
        }
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, int timeoutMs)
    {
        // Без тайм-аута сломанный резолвер подвесит весь цикл проверки.
        IPAddress[] all = await Dns.GetHostAddressesAsync(host)
            .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(500, timeoutMs)));

        bool isLocalhost = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

        IPAddress[] usable = all
            .Where(IsValidPingTarget)
            // 127.x.x.x вместо адреса компьютера — типичный признак ошибки в hosts/DNS.
            .Where(a => isLocalhost || !IPAddress.IsLoopback(a))
            // Link-local IPv6 без номера интерфейса пинговать нельзя.
            .Where(a => !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal))
            .Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Take(MaxCandidates)
            .ToArray();

        if (usable.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        return usable;
    }

    private static async Task<PingResult> PingCandidatesAsync(DnsEntry entry, int timeoutMs, int attempts, bool tryAll)
    {
        IPAddress? lastGood = entry.LastGood;
        IPAddress[] ordered = entry.Addresses;

        // Обычный случай — последний рабочий адрес уже первый: ничего не пересортировываем.
        if (lastGood != null && ordered.Length > 1 && !ordered[0].Equals(lastGood) && ordered.Contains(lastGood))
            ordered = ordered.OrderBy(a => a.Equals(lastGood) ? 0 : 1).ToArray();

        PingResult first = default;

        for (int i = 0; i < ordered.Length; i++)
        {
            // Основной адрес ждём полный тайм-аут, запасные — не дольше секунды.
            int timeout = i == 0 ? timeoutMs : Math.Min(timeoutMs, 1000);
            PingResult result = await PingWithRetryAsync(ordered[i], timeout, attempts);

            if (result.Ok)
            {
                entry.LastGood = ordered[i];
                return result;
            }

            if (i == 0)
                first = result;

            if (!tryAll)
                break;
        }

        return first;
    }

    private static async Task<PingResult> PingWithRetryAsync(IPAddress address, int timeoutMs, int attempts)
    {
        PingResult result = default;

        for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            result = await SendAsync(address, timeoutMs);
            if (result.Ok || result.Status == "Invalid IP address")
                return result;
        }

        return result;
    }

    private static async Task<PingResult> SendAsync(IPAddress address, int timeoutMs)
    {
        // Всегда возвращаем адрес, который пинговали. Адрес из ответа (reply.Address) использовать
        // нельзя: при «узел недоступен» отвечает шлюз или сам компьютер, и в плитке оказывался чужой IP.
        string target = address.ToString();

        Ping? ping = null;
        try
        {
            if (!PingPool.TryDequeue(out ping))
                ping = new Ping();

            PingReply reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);

            // Исправный объект возвращаем в пул; после исключения он будет уничтожен в finally.
            Ping toReturn = ping;
            ping = null;
            ReturnPing(toReturn);

            return reply.Status switch
            {
                IPStatus.Success => new PingResult(true, reply.RoundtripTime, "Online", target),
                IPStatus.TimedOut => new PingResult(false, null, "Timeout", target),
                _ => new PingResult(false, null, "Unreachable", target)
            };
        }
        catch (Exception ex) when (IsTimeoutException(ex))
        {
            // Тайм-аут — нормальный результат мониторинга, а не фатальная ошибка.
            return new PingResult(false, null, "Timeout", target);
        }
        catch (SocketException)
        {
            return new PingResult(false, null, "Unreachable", target);
        }
        catch (PingException)
        {
            return new PingResult(false, null, "Unreachable", target);
        }
        catch (ArgumentException)
        {
            return new PingResult(false, null, "Invalid IP address");
        }
        catch (Exception)
        {
            return new PingResult(false, null, "Check failed", target);
        }
        finally
        {
            // Сюда попадаем с непустым ping только после ошибки: такой объект не переиспользуем.
            ping?.Dispose();
        }
    }

    private static void ReturnPing(Ping ping)
    {
        if (PingPool.Count < MaxPooledPings)
            PingPool.Enqueue(ping);
        else
            ping.Dispose();
    }

    private static bool SameAddresses(IPAddress[] a, IPAddress[] b) =>
        a.Length == b.Length && !a.Except(b).Any();

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
