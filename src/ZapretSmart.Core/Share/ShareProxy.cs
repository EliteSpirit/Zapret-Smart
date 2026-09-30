using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ZapretSmart.Core.Share;

public sealed class ShareProxyOptions
{
    public int Port { get; init; } = ShareProxy.DefaultPort;
    public IPAddress BindAddress { get; init; } = IPAddress.Any;
    public int MaxClients { get; init; } = 256;
    public int MaxHeaderBytes { get; init; } = 16 * 1024;
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Сколько ждать вторую сторону, когда первая закончила передачу.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public Func<IPAddress, bool> AllowClient { get; init; } = NetworkPolicy.IsLocalClient;
    public Func<IPAddress, bool> AllowDestination { get; init; } = NetworkPolicy.IsAllowedDestination;
    public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = Dns.GetHostAddressesAsync;

    /// <summary>
    /// Локальные порты исходящих соединений. По ним движок раздачи отличает трафик прокси от трафика ПК
    /// (см. EngineScope.Share). null — порт выбирает система, так прокси работает в тестах и вне Windows.
    /// </summary>
    public (int Low, int High)? OutboundPorts { get; init; }

    /// <summary>Сеть точки доступа ПК для профиля iPhone; null — точка доступа выключена или неизвестна.</summary>
    public Func<WifiNetwork?> Wifi { get; init; } = () => null;
}

/// <summary>
/// HTTP-прокси для телефона. Соединения с сайтами открывает сам ПК, поэтому движок обхода видит их как свой трафик
/// и обрабатывает, как трафик браузера на ПК. Пересылаемый (транзитный) трафик раздачи Wi-Fi движок не видит.
/// Поддерживает CONNECT (HTTPS и любые TCP-протоколы) и обычные HTTP-запросы с полным адресом,
/// а по адресу /proxy.pac отдаёт файл автонастройки.
/// </summary>
public sealed class ShareProxy : IAsyncDisposable
{
    public const int DefaultPort = 8880;

    private readonly ShareProxyOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _active;
    private int _nextPort;

    public ShareProxy(ShareProxyOptions? options = null)
    {
        _options = options ?? new ShareProxyOptions();
        _slots = new SemaphoreSlim(_options.MaxClients);
    }

    /// <summary>Порт, на котором прокси слушает (при Port = 0 выбирается свободный).</summary>
    public int Port { get; private set; }

    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>Новое соединение от разрешённого клиента. Вызывается из фонового потока.</summary>
    public event Action<IPAddress>? ClientConnected;

    /// <summary>Отказ клиенту не из локальной сети. Вызывается из фонового потока.</summary>
    public event Action<IPAddress>? ClientRejected;

    /// <summary>
    /// Свободного порта в диапазоне не нашлось, соединение открыто с порта, который выбрала система.
    /// Такое соединение движок раздачи не видит. Вызывается из фонового потока.
    /// </summary>
    public event Action? OutboundPortsExhausted;

    /// <summary>
    /// Строка для журнала раздачи: что просило устройство и чем кончилось. Удачные соединения сообщаются один раз
    /// на пару «устройство, сервер», чтобы журнал не утонул. Вызывается из фонового потока.
    /// </summary>
    public event Action<IPAddress, string>? Logged;

    private const int MaxReportedTargets = 500;
    private readonly ConcurrentDictionary<string, byte> _reportedTargets = new();

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("прокси уже запущен");
        var listener = new TcpListener(_options.BindAddress, _options.Port);
        listener.Start(128);
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(listener, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (ct.IsCancellationRequested) return;
                continue;
            }

            var remote = ((IPEndPoint)client.RemoteEndPoint!).Address;
            if (!_options.AllowClient(remote))
            {
                ClientRejected?.Invoke(remote);
                client.Dispose();
                continue;
            }
            // Мест нет: закрываем сразу, а не копим очередь ждущих соединений.
            if (!_slots.Wait(0))
            {
                client.Dispose();
                continue;
            }
            ClientConnected?.Invoke(remote);
            _ = ServeAsync(client, remote, ct);
        }
    }

    private async Task ServeAsync(Socket client, IPAddress remote, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        Socket? upstream = null;
        try
        {
            client.NoDelay = true;
            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headerCts.CancelAfter(_options.HeaderTimeout);
            (string Head, byte[] Early)? head;
            try
            {
                head = await ReadHeadAsync(client, headerCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log(remote, $"не прислал запрос за {_options.HeaderTimeout.TotalSeconds:0} с, соединение закрыто");
                return;
            }
            if (head is null)
            {
                Log(remote, "слишком большой заголовок запроса, отказ");
                await ReplyAsync(client, 431, "Request Header Fields Too Large", ct);
                return;
            }

            var request = ProxyRequest.Parse(head.Value.Head);
            if (request is null)
            {
                Log(remote, "непонятный запрос: это не HTTP-прокси запрос");
                await ReplyAsync(client, 400, "Bad Request", ct);
                return;
            }

            if (request.Method == "GET" && request.Target.StartsWith('/'))
            {
                await ServeLocalAsync(client, remote, request, ct);
                return;
            }

            string host;
            int port;
            if (request.Method == "CONNECT")
            {
                if (!TrySplitHostPort(request.Target, 443, out host, out port))
                {
                    Log(remote, $"непонятный адрес в CONNECT: {Clean(request.Target)}");
                    await ReplyAsync(client, 400, "Bad Request", ct);
                    return;
                }
            }
            else if (Uri.TryCreate(request.Target, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host.Length > 0)
            {
                host = uri.IdnHost.Trim('[', ']');
                port = uri.Port;
            }
            else
            {
                Log(remote, $"запрос {Clean(request.Method)} не к http-адресу, отказ");
                await ReplyAsync(client, 400, "Bad Request", ct);
                return;
            }

            var target = $"{Clean(host)}:{port}";
            if (NetworkPolicy.BlockedPorts.Contains(port) || port == Port || port is <= 0 or > 65535)
            {
                Log(remote, $"{target}: порт запрещён (почта или сам прокси)");
                await ReplyAsync(client, 403, "Forbidden", ct);
                return;
            }

            IPAddress[] addresses;
            try
            {
                addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _options.Resolve(host, ct);
            }
            catch (SocketException)
            {
                Log(remote, $"{target}: имя не найдено в DNS");
                await ReplyAsync(client, 502, "Bad Gateway", ct);
                return;
            }
            var allowed = addresses.Where(_options.AllowDestination).ToArray();
            if (allowed.Length == 0)
            {
                Log(remote, addresses.Length == 0 ? $"{target}: имя не найдено в DNS" : $"{target}: адрес внутри ПК или сети, отказ");
                await ReplyAsync(client, addresses.Length == 0 ? 502 : 403, addresses.Length == 0 ? "Bad Gateway" : "Forbidden", ct);
                return;
            }

            upstream = await ConnectAsync(allowed, port, ct);
            if (upstream is null)
            {
                Log(remote, $"{target}: сервер не отвечает");
                await ReplyAsync(client, 502, "Bad Gateway", ct);
                return;
            }
            if (_reportedTargets.Count < MaxReportedTargets && _reportedTargets.TryAdd($"{remote} {target}", 0))
                Log(remote, $"открыл {target}");

            if (request.Method == "CONNECT")
            {
                await client.SendAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), SocketFlags.None, ct);
            }
            else
            {
                await upstream.SendAsync(Encoding.Latin1.GetBytes(request.ToOriginForm()), SocketFlags.None, ct);
            }
            // Клиент мог отправить данные сразу за заголовком, не дожидаясь ответа (тело запроса или начало TLS).
            if (head.Value.Early.Length > 0)
                await upstream.SendAsync(head.Value.Early, SocketFlags.None, ct);

            await PumpAsync(client, upstream, ct);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            upstream?.Dispose();
            client.Dispose();
            Interlocked.Decrement(ref _active);
            _slots.Release();
        }
    }

    /// <summary>Заголовок до пустой строки и то, что клиент успел прислать после него. null — заголовок слишком большой.</summary>
    private async Task<(string Head, byte[] Early)?> ReadHeadAsync(Socket client, CancellationToken ct)
    {
        var buffer = new byte[_options.MaxHeaderBytes];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length) return null;
            var read = await client.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, ct);
            if (read == 0) throw new IOException("клиент закрыл соединение");
            var searchFrom = Math.Max(0, filled - 3);
            filled += read;
            var end = buffer.AsSpan(searchFrom, filled - searchFrom).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            end += searchFrom + 4;
            return (Encoding.Latin1.GetString(buffer, 0, end), buffer[end..filled]);
        }
    }

    private async Task ServeLocalAsync(Socket client, IPAddress remote, ProxyRequest request, CancellationToken ct)
    {
        var path = request.Target.Split('?', 2)[0];
        if (path is "/i" or "/iphone.mobileconfig")
        {
            await ServeProfileAsync(client, remote, ct);
            return;
        }
        if (path != "/proxy.pac")
        {
            Log(remote, $"открыл адрес {Clean(path)}, такого нет (профиль iPhone: /i, автонастройка: /proxy.pac)");
            await ReplyAsync(client, 404, "Not Found", ct,
                "Zapret Smart: это прокси. Укажите этот адрес в настройках прокси телефона или файл автонастройки /proxy.pac.");
            return;
        }
        // Адрес берём из сокета, а не из заголовка Host: так в файл не попадёт ничего, что прислал клиент.
        var local = (IPEndPoint)client.LocalEndPoint!;
        var body = Pac(local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address, Port);
        var bytes = Encoding.UTF8.GetBytes(body);
        var headers = "HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig\r\n"
            + $"Content-Length: {bytes.Length}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
        await client.SendAsync(Encoding.ASCII.GetBytes(headers), SocketFlags.None, ct);
        await client.SendAsync(bytes, SocketFlags.None, ct);
        Log(remote, "забрал файл автонастройки proxy.pac");
    }

    /// <summary>
    /// Профиль iPhone: сеть точки доступа ПК с паролем и прокси. После установки iPhone сам подключается к этой сети
    /// уже с прокси. Пароль точки доступа отдаётся только клиентам из локальной сети (их пропускает AllowClient).
    /// </summary>
    private async Task ServeProfileAsync(Socket client, IPAddress remote, CancellationToken ct)
    {
        if (_options.Wifi() is not { } wifi)
        {
            Log(remote, "просил профиль iPhone, но имя сети Wi-Fi не задано");
            await ReplyAsync(client, 404, "Not Found", ct,
                "Zapret Smart: имя сети Wi-Fi неизвестно. Введите его в карточке раздачи в приложении и откройте этот адрес снова.");
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(IphoneProfile.Build(wifi, Port));
        var headers = "HTTP/1.1 200 OK\r\nContent-Type: application/x-apple-aspen-config\r\n"
            + "Content-Disposition: attachment; filename=\"ZapretSmart.mobileconfig\"\r\n"
            + $"Content-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await client.SendAsync(Encoding.ASCII.GetBytes(headers), SocketFlags.None, ct);
        await client.SendAsync(bytes, SocketFlags.None, ct);
        Log(remote, $"забрал профиль iPhone для сети «{Clean(wifi.Ssid)}»");
    }

    private void Log(IPAddress remote, string text) => Logged?.Invoke(remote, text);

    /// <summary>То, что прислало устройство, в журнал попадает коротким и без управляющих символов.</summary>
    internal static string Clean(string text)
    {
        var chars = text.Where(c => !char.IsControl(c)).Take(80).ToArray();
        return new string(chars) + (text.Length > 80 ? "…" : "");
    }

    /// <summary>
    /// Файл автонастройки: всё через ПК, а если ПК недоступен (выключен, ушли из дома), напрямую.
    /// Локальные имена всегда напрямую.
    /// </summary>
    public static string Pac(IPAddress address, int port) =>
        "function FindProxyForURL(url, host) {\n"
        + "  if (isPlainHostName(host) || dnsDomainIs(host, \".local\")) return \"DIRECT\";\n"
        + $"  return \"PROXY {address}:{port}; DIRECT\";\n"
        + "}\n";

    /// <summary>Сколько портов диапазона пробовать для одного соединения, прежде чем отдать выбор системе.</summary>
    private const int PortAttempts = 64;

    private async Task<Socket?> ConnectAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        var range = _options.OutboundPorts;
        foreach (var address in addresses)
        {
            // С диапазоном: до PortAttempts портов из него, затем одна попытка с портом от системы.
            for (var attempt = 0; ; attempt++)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                var fromRange = range is not null && attempt < PortAttempts;
                if (fromRange && !TryBind(socket, range!.Value))
                {
                    socket.Dispose();
                    continue;
                }
                if (range is not null && !fromRange) OutboundPortsExhausted?.Invoke();

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_options.ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(address, port, connectCts.Token);
                    return socket;
                }
                catch (SocketException e) when (fromRange && e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    // Порт ещё держит прежнее соединение с тем же сайтом (TIME_WAIT): берём следующий.
                    socket.Dispose();
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    if (ct.IsCancellationRequested) throw;
                    break; // этот адрес не отвечает, пробуем следующий
                }
            }
        }
        return null;
    }

    /// <summary>Привязывает сокет к следующему порту диапазона по кругу. false — порт занят.</summary>
    private bool TryBind(Socket socket, (int Low, int High) range)
    {
        var size = range.High - range.Low + 1;
        var offset = (int)((uint)Interlocked.Increment(ref _nextPort) % (uint)size);
        var any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        try
        {
            socket.Bind(new IPEndPoint(any, range.Low + offset));
            return true;
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            return false;
        }
    }

    /// <summary>
    /// Перекачка в обе стороны. Когда одна сторона закончила, её конец закрывается на запись (FIN), а вторая
    /// ещё дочитывает ответ; если она молчит дольше DrainTimeout, соединение закрывается целиком.
    /// </summary>
    private async Task PumpAsync(Socket client, Socket upstream, CancellationToken ct)
    {
        var up = CopyAsync(client, upstream, ct);
        var down = CopyAsync(upstream, client, ct);
        var first = await Task.WhenAny(up, down);
        var rest = first == up ? down : up;
        await Task.WhenAny(rest, Task.Delay(_options.DrainTimeout, ct));
    }

    private static async Task CopyAsync(Socket from, Socket to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (read == 0) break;
                await to.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, ct);
            }
            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static async Task ReplyAsync(Socket client, int code, string reason, CancellationToken ct, string? text = null)
    {
        var body = Encoding.UTF8.GetBytes(text ?? $"{code} {reason}");
        var headers = $"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        try
        {
            await client.SendAsync(Encoding.ASCII.GetBytes(headers), SocketFlags.None, ct);
            await client.SendAsync(body, SocketFlags.None, ct);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>host:port, [v6]:port или просто host.</summary>
    public static bool TrySplitHostPort(string target, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        if (target.StartsWith('['))
        {
            var close = target.IndexOf(']');
            if (close < 0) return false;
            host = target[1..close];
            var tail = target[(close + 1)..];
            if (tail.Length > 0 && (!tail.StartsWith(':') || !int.TryParse(tail[1..], out port))) return false;
        }
        else
        {
            var colon = target.LastIndexOf(':');
            if (colon >= 0)
            {
                if (target.IndexOf(':') != colon || !int.TryParse(target[(colon + 1)..], out port)) return false;
                host = target[..colon];
            }
            else
            {
                host = target;
            }
        }
        return host.Length > 0 && port is > 0 and <= 65535
            && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_');
    }
}

/// <summary>Первая строка и заголовки запроса к прокси.</summary>
public sealed record ProxyRequest(string Method, string Target, string Version, IReadOnlyList<(string Name, string Value)> Headers)
{
    /// <summary>Заголовки, которые относятся к соединению с прокси и дальше не передаются.</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Proxy-Connection", "Proxy-Authorization", "Connection", "Keep-Alive", "Upgrade", "TE", "Trailer",
    };

    public static ProxyRequest? Parse(string head)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || first[0].Length == 0
            || !first[0].All(char.IsAsciiLetterUpper) || first[1].Length == 0)
            return null;
        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return new ProxyRequest(first[0], first[1], first[2], headers);
    }

    /// <summary>
    /// Запрос для сайта: путь вместо полного адреса, без заголовков прокси, с Connection: close.
    /// Одно соединение на запрос: так не нужно разбирать тела и ответы, а клиент сам откроет новое.
    /// </summary>
    public string ToOriginForm()
    {
        var uri = new Uri(Target);
        var sb = new StringBuilder();
        sb.Append(Method).Append(' ').Append(uri.PathAndQuery).Append(' ').Append(Version).Append("\r\n");
        var hasHost = false;
        foreach (var (name, value) in Headers)
        {
            if (HopByHop.Contains(name)) continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hasHost = true;
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        if (!hasHost) sb.Append("Host: ").Append(uri.Authority).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        return sb.ToString();
    }
}
