using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ZapretSmart.Core.Share;

namespace ZapretSmart.Core.Tests;

/// <summary>
/// Прокси для раздачи обхода на телефон: настоящие сокеты на localhost. Тестовый «сайт» слушает на 127.0.0.1,
/// поэтому в большинстве тестов проверка назначения ослаблена; её поведение по умолчанию проверяется отдельно.
/// </summary>
public sealed class ShareTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _cleanup = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _cleanup) await c.DisposeAsync();
    }

    private ShareProxy Proxy(ShareProxyOptions? options = null)
    {
        var proxy = new ShareProxy(options ?? new ShareProxyOptions
        {
            Port = 0,
            BindAddress = IPAddress.Loopback,
            AllowDestination = _ => true,
        });
        proxy.Start();
        _cleanup.Add(proxy);
        return proxy;
    }

    /// <summary>«Сайт»: принимает одно соединение и отдаёт его тесту.</summary>
    private static (int Port, Task<Socket> Accepted) Site()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return (port, AcceptOnce(listener));

        static async Task<Socket> AcceptOnce(TcpListener l)
        {
            try
            {
                return await l.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                l.Stop();
            }
        }
    }

    private static async Task<Socket> Connect(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await s.ConnectAsync(IPAddress.Loopback, port);
        return s;
    }

    private static async Task Send(Socket s, string text) => await s.SendAsync(Encoding.Latin1.GetBytes(text), SocketFlags.None);

    /// <summary>Читает, пока не придёт маркер или сокет не закроется.</summary>
    private static async Task<string> ReadUntil(Socket s, string marker, bool toEnd = false)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (toEnd || !sb.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await s.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            if (read == 0) break;
            sb.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
        return sb.ToString();
    }

    [Fact]
    public async Task ConnectTunnelCarriesBytesBothWaysIncludingDataSentBeforeTheReply()
    {
        var proxy = Proxy();
        var (sitePort, accepted) = Site();
        using var phone = await Connect(proxy.Port);

        // Некоторые клиенты шлют начало TLS сразу за CONNECT, не дожидаясь ответа прокси.
        await Send(phone, $"CONNECT 127.0.0.1:{sitePort} HTTP/1.1\r\nHost: 127.0.0.1:{sitePort}\r\n\r\nHELLO");
        using var site = await accepted;
        Assert.StartsWith("HTTP/1.1 200", await ReadUntil(phone, "\r\n\r\n"));
        Assert.Equal("HELLO", await ReadUntil(site, "HELLO"));

        await Send(site, "WORLD");
        Assert.Equal("WORLD", await ReadUntil(phone, "WORLD"));

        // Телефон закончил передачу: сайт видит конец потока и ещё может ответить.
        phone.Shutdown(SocketShutdown.Send);
        Assert.Equal("", await ReadUntil(site, "", toEnd: true));
        await Send(site, "BYE");
        site.Shutdown(SocketShutdown.Send);
        Assert.Equal("BYE", await ReadUntil(phone, "", toEnd: true));
    }

    /// <summary>Свободный диапазон из нескольких портов для теста (подряд свободных портов ищем пробой).</summary>
    private static (int Low, int High) FreeRange(int size)
    {
        for (var low = 41000; low < 44000; low += size)
        {
            var sockets = new List<Socket>();
            try
            {
                for (var p = low; p < low + size; p++)
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    sockets.Add(s);
                    s.Bind(new IPEndPoint(IPAddress.Any, p));
                }
                return (low, low + size - 1);
            }
            catch (SocketException)
            {
            }
            finally
            {
                foreach (var s in sockets) s.Dispose();
            }
        }
        throw new InvalidOperationException("нет свободного диапазона");
    }

    /// <summary>По исходящему порту движок раздачи отличает трафик прокси от трафика ПК.</summary>
    [Fact]
    public async Task OutgoingConnectionsLeaveFromTheShareRange()
    {
        var range = FreeRange(8);
        var proxy = Proxy(new ShareProxyOptions
        {
            Port = 0, BindAddress = IPAddress.Loopback, AllowDestination = _ => true, OutboundPorts = range,
        });
        for (var i = 0; i < 3; i++)
        {
            var (sitePort, accepted) = Site();
            using var phone = await Connect(proxy.Port);
            await Send(phone, $"CONNECT 127.0.0.1:{sitePort} HTTP/1.1\r\n\r\n");
            using var site = await accepted;
            Assert.InRange(((IPEndPoint)site.RemoteEndPoint!).Port, range.Low, range.High);
            Assert.StartsWith("HTTP/1.1 200", await ReadUntil(phone, "\r\n\r\n"));
        }
    }

    [Fact]
    public async Task WhenTheRangeIsFullTheConnectionStillWorksAndIsReported()
    {
        var range = FreeRange(2);
        using var a = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var b = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Слушающий сокет занимает порт на обеих ОС: .NET на Linux ставит SO_REUSEADDR, и просто привязанный не мешал бы.
        a.Bind(new IPEndPoint(IPAddress.Any, range.Low));
        b.Bind(new IPEndPoint(IPAddress.Any, range.High));
        a.Listen();
        b.Listen();
        var exhausted = 0;
        var proxy = Proxy(new ShareProxyOptions
        {
            Port = 0, BindAddress = IPAddress.Loopback, AllowDestination = _ => true, OutboundPorts = range,
        });
        proxy.OutboundPortsExhausted += () => Interlocked.Increment(ref exhausted);

        var (sitePort, accepted) = Site();
        using var phone = await Connect(proxy.Port);
        await Send(phone, $"CONNECT 127.0.0.1:{sitePort} HTTP/1.1\r\n\r\n");
        using var site = await accepted;
        Assert.StartsWith("HTTP/1.1 200", await ReadUntil(phone, "\r\n\r\n"));
        Assert.Equal(1, exhausted);
    }

    [Fact]
    public async Task PlainHttpIsForwardedWithAPathAndWithoutProxyHeaders()
    {
        var proxy = Proxy();
        var (sitePort, accepted) = Site();
        using var phone = await Connect(proxy.Port);

        await Send(phone, $"POST http://127.0.0.1:{sitePort}/a/b?c=1 HTTP/1.1\r\nHost: 127.0.0.1:{sitePort}\r\n"
            + "Proxy-Connection: keep-alive\r\nProxy-Authorization: Basic eDp5\r\nContent-Length: 4\r\n\r\nbody");
        using var site = await accepted;
        var request = await ReadUntil(site, "body");

        Assert.StartsWith("POST /a/b?c=1 HTTP/1.1\r\n", request);
        Assert.Contains($"Host: 127.0.0.1:{sitePort}\r\n", request);
        Assert.Contains("Connection: close\r\n", request);
        Assert.DoesNotContain("Proxy-", request);
        Assert.EndsWith("\r\n\r\nbody", request);

        await Send(site, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        site.Shutdown(SocketShutdown.Send);
        var response = await ReadUntil(phone, "", toEnd: true);
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.EndsWith("ok", response);
    }

    [Fact]
    public async Task ByDefaultThePcItselfMailPortsAndTheProxyPortAreOffLimits()
    {
        var proxy = Proxy(new ShareProxyOptions { Port = 0, BindAddress = IPAddress.Loopback });
        foreach (var target in new[] { "127.0.0.1:445", "localhost:3389", "[::1]:80", "8.8.8.8:25", "8.8.8.8:587", $"example.com:{proxy.Port}" })
        {
            using var phone = await Connect(proxy.Port);
            await Send(phone, $"CONNECT {target} HTTP/1.1\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 403", await ReadUntil(phone, "\r\n\r\n"));
        }
    }

    [Fact]
    public async Task ClientsOutsideTheLocalNetworkAreDroppedWithoutAReply()
    {
        IPAddress? rejected = null;
        var proxy = Proxy(new ShareProxyOptions { Port = 0, BindAddress = IPAddress.Loopback, AllowClient = _ => false });
        proxy.ClientRejected += ip => rejected = ip;
        using var stranger = await Connect(proxy.Port);
        await Send(stranger, "CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        // Linux закрывает такое соединение обычным FIN, Windows сбросом (клиент прислал непрочитанные данные).
        // В обоих случаях ответа нет, и это главное.
        string reply;
        try
        {
            reply = await ReadUntil(stranger, "", toEnd: true);
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
        {
            reply = "";
        }
        Assert.Equal("", reply);
        Assert.Equal(IPAddress.Loopback, rejected);
    }

    [Fact]
    public async Task PacFilePointsAtTheAddressThePhoneUsedAndFallsBackToDirect()
    {
        var proxy = Proxy();
        using var phone = await Connect(proxy.Port);
        await Send(phone, "GET /proxy.pac HTTP/1.1\r\nHost: evil\";alert(1)//\r\n\r\n");
        var response = await ReadUntil(phone, "", toEnd: true);
        Assert.Contains("application/x-ns-proxy-autoconfig", response);
        Assert.Contains($"return \"PROXY 127.0.0.1:{proxy.Port}; DIRECT\";", response);
        Assert.DoesNotContain("evil", response);
    }

    /// <summary>Каждый исход запроса попадает в журнал: по нему видно, дошёл ли телефон до ПК и что ему ответили.</summary>
    [Fact]
    public async Task EveryRequestOutcomeIsLogged()
    {
        var site = Site();
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var proxy = Proxy(new ShareProxyOptions
        {
            Port = 0, BindAddress = IPAddress.Loopback, AllowDestination = _ => true, HeaderTimeout = TimeSpan.FromMilliseconds(300),
            Resolve = (host, _) => host == "site.test" ? Task.FromResult(new[] { IPAddress.Loopback })
                : throw new SocketException((int)SocketError.HostNotFound),
        });
        proxy.Logged += (ip, text) => logged.Enqueue($"{ip} {text}");

        async Task<string> Ask(string request)
        {
            using var phone = await Connect(proxy.Port);
            await Send(phone, request);
            return await ReadUntil(phone, "", toEnd: true);
        }

        await Ask("GET /proxy.pac HTTP/1.1\r\n\r\n");
        await Ask("GET /i HTTP/1.1\r\n\r\n");
        await Ask("GET /favicon.ico HTTP/1.1\r\n\r\n");
        await Ask("CONNECT nowhere.invalid:443 HTTP/1.1\r\n\r\n");
        await Ask("CONNECT example.com:25 HTTP/1.1\r\n\r\n");
        await Ask("GARBAGE\r\n\r\n");
        await Ask("CONNECT slow.test:443 HTTP/1.1\r\n");
        using (var phone = await Connect(proxy.Port))
        {
            await Send(phone, $"CONNECT site.test:{site.Port} HTTP/1.1\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 200", await ReadUntil(phone, "\r\n\r\n"));
            (await site.Accepted).Dispose();
        }

        var lines = logged.ToArray();
        Assert.All(lines, l => Assert.StartsWith("127.0.0.1 ", l));
        Assert.Contains(lines, l => l.Contains("забрал файл автонастройки proxy.pac"));
        Assert.Contains(lines, l => l.Contains("просил профиль iPhone, но имя сети Wi-Fi не задано"));
        Assert.Contains(lines, l => l.Contains("открыл адрес /favicon.ico, такого нет"));
        Assert.Contains(lines, l => l.Contains("nowhere.invalid:443: имя не найдено в DNS"));
        Assert.Contains(lines, l => l.Contains("example.com:25: порт запрещён"));
        Assert.Contains(lines, l => l.Contains("непонятный запрос"));
        Assert.Contains(lines, l => l.Contains("не прислал запрос за 0 с"));
        Assert.Contains(lines, l => l.Contains($"открыл site.test:{site.Port}"));
    }

    [Fact]
    public async Task SuccessfulConnectionsAreLoggedOncePerServer()
    {
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var proxy = Proxy();
        proxy.Logged += (_, text) => logged.Enqueue(text);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                using var phone = await Connect(proxy.Port);
                await Send(phone, $"CONNECT 127.0.0.1:{port} HTTP/1.1\r\n\r\n");
                Assert.StartsWith("HTTP/1.1 200", await ReadUntil(phone, "\r\n\r\n"));
                (await listener.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
            }
        }
        finally
        {
            listener.Stop();
        }
        var other = Site();
        using (var phone = await Connect(proxy.Port))
        {
            await Send(phone, $"CONNECT 127.0.0.1:{other.Port} HTTP/1.1\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 200", await ReadUntil(phone, "\r\n\r\n"));
            (await other.Accepted).Dispose();
        }
        Assert.Single(logged, $"открыл 127.0.0.1:{port}");
        Assert.Single(logged, $"открыл 127.0.0.1:{other.Port}");
    }

    [Fact]
    public void ClientTextIsShortenedAndStrippedBeforeLogging()
    {
        Assert.Equal("ab", ShareProxy.Clean("a\r\nb"));
        var cleaned = ShareProxy.Clean(new string('x', 200));
        Assert.Equal(81, cleaned.Length);
        Assert.EndsWith("…", cleaned);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void ShareLogWritesTimedLinesAndCollapsesOnlyRequestedRepeats()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zs-sharelog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var time = new ManualTime(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
            var log = new ShareLog(Path.Combine(dir, "share.log"), time);
            Assert.True(log.Write("192.168.1.5: сервер не отвечает", collapseRepeats: true));
            Assert.False(log.Write("192.168.1.5: сервер не отвечает", collapseRepeats: true));
            Assert.True(log.Write("движок остановлен"));
            Assert.True(log.Write("движок остановлен"));
            time.Now += ShareLog.RepeatInterval;
            Assert.True(log.Write("192.168.1.5: сервер не отвечает", collapseRepeats: true));

            var lines = File.ReadAllLines(log.FilePath);
            Assert.Equal(
            [
                "2026-09-30 12:00:00 192.168.1.5: сервер не отвечает",
                "2026-09-30 12:00:00 движок остановлен",
                "2026-09-30 12:00:00 движок остановлен",
                "2026-09-30 12:01:00 192.168.1.5: сервер не отвечает",
            ], lines);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShareLogRollsOverPastOneMegabyte()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zs-sharelog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new ShareLog(Path.Combine(dir, "share.log"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(log.FilePath, new string('x', (int)ShareLog.MaxBytes + 1));
            log.Write("после ротации");
            Assert.Equal(ShareLog.MaxBytes + 1, new FileInfo(log.OldFilePath).Length);
            Assert.EndsWith("после ротации", File.ReadAllText(log.FilePath).TrimEnd());
            Assert.Equal(Path.Combine(dir, "share.old.log"), log.OldFilePath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] Ipv4Tcp(string source)
    {
        var p = new byte[40];
        p[0] = 0x45;
        p[9] = 6;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(p, 12);
        IPAddress.Parse("192.168.31.40").GetAddressBytes().CopyTo(p, 16);
        return p;
    }

    [Fact]
    public void SynSourceIsTheSenderOfAnIpv4TcpPacket()
    {
        Assert.Equal(IPAddress.Parse("192.168.31.77"), ShareReachProbe.SynSource(Ipv4Tcp("192.168.31.77")));
        var udp = Ipv4Tcp("192.168.31.77");
        udp[9] = 17;
        Assert.Null(ShareReachProbe.SynSource(udp));
        Assert.Null(ShareReachProbe.SynSource(new byte[10]));
        Assert.Equal("inbound and !loopback and ip and tcp.DstPort == 8880 and tcp.Syn and !tcp.Ack", ShareReachProbe.SynFilter(8880));
    }

    [Theory]
    [InlineData("Firewall Policy                       BlockInboundAlways,AllowOutbound", true)]
    [InlineData("Политика брандмауэра                  BlockInboundAlways,AllowOutbound", true)]
    [InlineData("Firewall Policy                       BlockInbound,AllowOutbound", false)]
    public void BlockAllInboundIsRecognisedInAnyLanguage(string output, bool blocks) =>
        Assert.Equal(blocks, ShareReachProbe.BlocksAllInbound(output));

    private static ReachReport Reach(bool self = true, FirewallState? firewall = null, Dictionary<IPAddress, int>? syns = null,
        IPAddress[]? connected = null, string? sniffError = null, bool loopback = true, string[]? interceptors = null) =>
        new(8880, "192.168.31.40", TimeSpan.FromSeconds(60), self, loopback, interceptors ?? [], firewall ?? FirewallState.Unknown,
            syns ?? [], connected ?? [], sniffError);

    /// <summary>
    /// Случай пользователя: прокси слушает, а подключение к своему адресу в сети пропадает. Kaspersky был на паузе и
    /// оказался ни при чём, поэтому вывод различает «прокси не принимает» и «режет фильтр», а чужой WinDivert называет первым.
    /// </summary>
    [Fact]
    public void ReachVerdictSeparatesADeadProxyFromAFilterAndNamesForeignInterceptors()
    {
        var filter = ShareReachProbe.Explain(Reach(self: false, firewall: new FirewallState(false, ["Kaspersky"])));
        Assert.Contains("на 127.0.0.1 порт 8880 отвечает", filter[0]);
        Assert.Contains("192.168.31.40", filter[0]);
        Assert.Contains(filter, l => l.Contains("Kaspersky") && l.Contains("«Выход»"));

        var dead = ShareReachProbe.Explain(Reach(self: false, loopback: false));
        Assert.Contains("ни на адресе в сети, ни на 127.0.0.1", dead[0]);

        var other = ShareReachProbe.Explain(Reach(self: false, interceptors: ["winws.exe (PID 42)"]));
        Assert.Contains("другой перехватчик пакетов: winws.exe (PID 42)", other[0]);

        var syns = ShareReachProbe.Explain(Reach(syns: new() { [IPAddress.Parse("192.168.31.77")] = 1 }, interceptors: ["goodbyedpi.exe (PID 7)"]));
        Assert.Contains(syns, l => l.Contains("goodbyedpi.exe"));
        Assert.DoesNotContain(syns, l => l.Contains("групповая политика"));
    }

    [Fact]
    public void ForeignInterceptorsAreOtherDpiToolsOutsideOurEngineFolder()
    {
        var own = OperatingSystem.IsWindows() ? @"C:\Apps\ZapretSmart\engine" : "/apps/zs/engine";
        var ours = OperatingSystem.IsWindows() ? @"C:\Apps\ZapretSmart\engine\winws.exe" : "/apps/zs/engine/winws";
        var theirs = OperatingSystem.IsWindows() ? @"C:\zapret-discord-youtube\bin\winws.exe" : "/opt/zapret/winws";
        var found = ShareReachProbe.ForeignInterceptors(
            [("winws", ours, 1), ("winws", theirs, 2), ("GoodbyeDPI", null, 3), ("chrome", "/x/chrome", 4)], own);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.StartsWith("winws.exe (PID 2"));
        Assert.Contains(found, f => f == "GoodbyeDPI.exe (PID 3)");
    }

    [Fact]
    public void ShareLogHidesTheUserProfileFolder()
    {
        Assert.Equal(@"--hostlist=%USERPROFILE%\AppData\Roaming\ZapretSmart\lists\blocked.txt",
            ShareLog.Redact(@"--hostlist=C:\Users\artem\AppData\Roaming\ZapretSmart\lists\blocked.txt", @"C:\Users\artem"));
        Assert.Equal("x %USERPROFILE%\\a", ShareLog.Redact(@"x c:\users\ARTEM\a", @"C:\Users\artem\"));
        Assert.Equal("без путей", ShareLog.Redact("без путей", @"C:\Users\artem"));
    }

    [Fact]
    public void ReachVerdictNamesTheBrokenLeg()
    {
        var phone = IPAddress.Parse("192.168.31.77");
        Assert.Contains("пропадают даже с самого ПК", ShareReachProbe.Explain(Reach(self: false))[0]);
        Assert.Contains("Связь в порядке: 192.168.31.77", ShareReachProbe.Explain(Reach(connected: [phone]))[0]);

        var nothing = ShareReachProbe.Explain(Reach());
        Assert.Contains("не дошло ни одной попытки", nothing[0]);
        Assert.Contains("изоляция клиентов", nothing[1]);
        Assert.Contains("192.168.31.40:8880", nothing[1]);

        var blocked = ShareReachProbe.Explain(Reach(syns: new() { [phone] = 3 }));
        Assert.Contains("отбрасывает брандмауэр на ПК", blocked[0]);
        Assert.Contains("192.168.31.77 (3)", blocked[0]);
        Assert.Contains("групповая политика", blocked[^1]);

        var shields = ShareReachProbe.Explain(Reach(firewall: new FirewallState(true, ["Kaspersky"]), syns: new() { [phone] = 1 }));
        Assert.Contains(shields, l => l.Contains("Блокировать все входящие"));
        Assert.Contains(shields, l => l.Contains("Kaspersky") && l.Contains("порт 8880"));
        Assert.DoesNotContain(shields, l => l.Contains("групповая политика"));

        Assert.Contains("не удалось: драйвер", ShareReachProbe.Explain(Reach(sniffError: "драйвер"))[0]);
        Assert.All(ShareReachProbe.Explain(Reach(firewall: new FirewallState(false, ["ESET"]))), l => Assert.DoesNotContain("—", l));
    }

    [Fact]
    public async Task SelfConnectTellsAListeningPortFromAClosedOne()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await ShareReachProbe.SelfConnectsAsync(IPAddress.Loopback, port, CancellationToken.None));
        listener.Stop();

        // Порт занят сокетом без Listen: подключение получает отказ, и параллельный тест этот порт не займёт.
        using var closed = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        closed.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var closedPort = ((IPEndPoint)closed.LocalEndPoint!).Port;
        Assert.False(await ShareReachProbe.SelfConnectsAsync(IPAddress.Loopback, closedPort, CancellationToken.None));
    }

    [Theory]
    [InlineData("\r\nDeleted 2 rule(s).\r\nOk.\r\n", 2)]
    [InlineData("\r\nУдалено правил: 3.\r\nОК.\r\n", 3)]
    [InlineData("Ok.", 1)]
    public void DeletedFirewallRulesAreCounted(string output, int count) => Assert.Equal(count, ShareFirewall.CountDeleted(output));

    [Theory]
    [InlineData("GARBAGE\r\n\r\n", "400")]
    [InlineData("CONNECT no-port-here:x HTTP/1.1\r\n\r\n", "400")]
    [InlineData("GET https://example.com/ HTTP/1.1\r\n\r\n", "400")]
    [InlineData("GET /other HTTP/1.1\r\n\r\n", "404")]
    public async Task MalformedOrUnknownRequestsGetAnErrorNotAHang(string request, string code)
    {
        var proxy = Proxy();
        using var phone = await Connect(proxy.Port);
        await Send(phone, request);
        Assert.StartsWith("HTTP/1.1 " + code, await ReadUntil(phone, "\r\n\r\n"));
    }

    [Fact]
    public async Task OversizedAndSlowHeadersAreCutOff()
    {
        var proxy = Proxy(new ShareProxyOptions
        {
            Port = 0, BindAddress = IPAddress.Loopback, MaxHeaderBytes = 256, HeaderTimeout = TimeSpan.FromMilliseconds(300),
        });

        using (var big = await Connect(proxy.Port))
        {
            await Send(big, "GET http://example.com/ HTTP/1.1\r\nX: " + new string('a', 400));
            Assert.StartsWith("HTTP/1.1 431", await ReadUntil(big, "\r\n\r\n"));
        }

        using var slow = await Connect(proxy.Port);
        await Send(slow, "CONNECT example.com:443 HTTP/1.1\r\n");
        var sw = Stopwatch.StartNew();
        Assert.Equal("", await ReadUntil(slow, "", toEnd: true));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        // Сокет закрывается раньше, чем прокси уменьшает счётчик в finally: ждём, а не проверяем в ту же миллисекунду.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (proxy.ActiveConnections != 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(0, proxy.ActiveConnections);
    }

    [Fact]
    public async Task UnresolvableHostIsABadGateway()
    {
        var proxy = Proxy(new ShareProxyOptions
        {
            Port = 0, BindAddress = IPAddress.Loopback,
            Resolve = (_, _) => throw new SocketException((int)SocketError.HostNotFound),
        });
        using var phone = await Connect(proxy.Port);
        await Send(phone, "CONNECT nowhere.invalid:443 HTTP/1.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 502", await ReadUntil(phone, "\r\n\r\n"));
    }

    [Theory]
    [InlineData("192.168.137.23", true)]
    [InlineData("192.168.1.5", true)]
    [InlineData("10.0.0.2", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::ffff:192.168.0.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::5", true)]
    [InlineData("100.64.1.2", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2a00:1450::1", false)]
    public void OnlyLocalNetworkClientsAreAccepted(string address, bool allowed) =>
        Assert.Equal(allowed, NetworkPolicy.IsLocalClient(IPAddress.Parse(address)));

    [Theory]
    [InlineData("142.250.74.14", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.8.8.8", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2a00:1450::1", true)]
    public void DestinationPolicyKeepsThePcsOwnServicesClosed(string address, bool allowed) =>
        Assert.Equal(allowed, NetworkPolicy.IsAllowedDestination(IPAddress.Parse(address)));

    [Theory]
    [InlineData("example.com:443", "example.com", 443)]
    [InlineData("example.com", "example.com", 443)]
    [InlineData("[2a00::1]:8443", "2a00::1", 8443)]
    public void HostAndPortAreSplit(string target, string host, int port)
    {
        Assert.True(ShareProxy.TrySplitHostPort(target, 443, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("example.com:0")]
    [InlineData("example.com:70000")]
    [InlineData("a:b:c")]
    [InlineData("exa mple.com:443")]
    [InlineData("[::1")]
    [InlineData(":443")]
    public void BadTargetsAreRejected(string target) => Assert.False(ShareProxy.TrySplitHostPort(target, 443, out _, out _));

    [Fact]
    public void HotspotOutputIsParsedAndErrorsAreExplained()
    {
        var on = WindowsHotspot.Parse("state: On\r\nssid: Мой ПК 42\r\npassphrase: pa ss: word\r\nclients: 2\r\n");
        Assert.Equal(new HotspotState(true, "Мой ПК 42", "pa ss: word", 2, null), on);
        Assert.False(WindowsHotspot.Parse("state: Off\nssid: x\npassphrase: y\nclients: 0\n").IsOn);
        Assert.Equal("в этом компьютере нет Wi-Fi, который умеет раздавать", WindowsHotspot.Parse("error: TechnologyNotAvailable\n").Error);
        Assert.Equal("Не найден элемент.", WindowsHotspot.Parse("error: Не найден элемент.\n").Error);
        Assert.NotNull(WindowsHotspot.Parse("").Error);
    }

    private static System.Xml.Linq.XDocument ParsePlist(string xml) =>
        System.Xml.Linq.XDocument.Load(System.Xml.XmlReader.Create(new StringReader(xml),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore }));

    /// <summary>Значение ключа в первом словаре plist, где он встречается.</summary>
    private static string? PlistValue(System.Xml.Linq.XDocument doc, string key) =>
        doc.Descendants("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().First() is { } v
            ? (v.Name.LocalName is "true" or "false" ? v.Name.LocalName : v.Value)
            : null;

    [Fact]
    public void IphoneProfileCarriesTheHotspotAndProxyAndEscapesText()
    {
        var wifi = new WifiNetwork("ПК <Дом> & \"Ко\"", "p<a>ss&'1", IPAddress.Parse("192.168.137.1"));
        var xml = IphoneProfile.Build(wifi, 8880);
        var doc = ParsePlist(xml);

        Assert.Equal("com.apple.wifi.managed", PlistValue(doc, "PayloadType"));
        Assert.Equal(wifi.Ssid, PlistValue(doc, "SSID_STR"));
        Assert.Equal(wifi.Passphrase, PlistValue(doc, "Password"));
        // Автонастройка с запасным «напрямую», а не ручной прокси: без приложения iPhone не должен остаться без интернета.
        Assert.Equal("Auto", PlistValue(doc, "ProxyType"));
        Assert.Equal("http://192.168.137.1:8880/proxy.pac", PlistValue(doc, "ProxyPACURL"));
        Assert.Equal("true", PlistValue(doc, "ProxyPACFallbackAllowed"));
        Assert.Null(PlistValue(doc, "ProxyServer"));
        Assert.Equal("true", PlistValue(doc, "AutoJoin"));

        // Повторная установка должна заменить профиль, а не добавить второй: UUID и идентификатор постоянные.
        var again = ParsePlist(IphoneProfile.Build(wifi with { Passphrase = "other" }, 9000));
        var uuids = doc.Descendants("key").Where(k => k.Value == "PayloadUUID").Select(k => k.ElementsAfterSelf().First().Value).ToList();
        Assert.Equal(2, uuids.Distinct().Count());
        Assert.Equal(uuids, again.Descendants("key").Where(k => k.Value == "PayloadUUID").Select(k => k.ElementsAfterSelf().First().Value));
        Assert.All(uuids, u => Assert.True(Guid.TryParse(u, out _)));
    }

    private static string WlanProfile(string auth, string? key, bool isProtected) => $"""
        <?xml version="1.0"?>
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
          <name>Дом 5G</name>
          <SSIDConfig><SSID><hex>D094D0BED0BC2035 47</hex><name>Дом 5G</name></SSID></SSIDConfig>
          <connectionType>ESS</connectionType>
          <MSM><security>
            <authEncryption><authentication>{auth}</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
            {(key is null ? "" : $"<sharedKey><keyType>passPhrase</keyType><protected>{(isProtected ? "true" : "false")}</protected><keyMaterial>{key}</keyMaterial></sharedKey>")}
          </security></MSM>
        </WLANProfile>
        """;

    [Fact]
    public void HomeWifiPasswordIsReadOnlyWhenWindowsGivesItInPlainText()
    {
        Assert.Equal(new WifiCredentials("Дом 5G", "k7P9x2mQ", false, true), WindowsWifi.ParseProfile(WlanProfile("WPA2PSK", "k7P9x2mQ", false)));
        Assert.Equal(new WifiCredentials("Дом 5G", "pass word", false, true), WindowsWifi.ParseProfile(WlanProfile("WPA3SAE", "pass word", false)));
        // Без прав администратора Windows отдаёт зашифрованный блок: телефону он бесполезен.
        Assert.Null(WindowsWifi.ParseProfile(WlanProfile("WPA2PSK", "01000000D08C9DDF0115D1118C7A00C0", true)).Passphrase);
        Assert.Equal(new WifiCredentials("Дом 5G", null, true, true), WindowsWifi.ParseProfile(WlanProfile("open", null, false)));
        // Корпоративная сеть с логином: профиль iPhone без настроек EAP для неё не подойдёт.
        Assert.False(WindowsWifi.ParseProfile(WlanProfile("WPA2", null, false)).IsPersonal);
    }

    [Fact]
    public void ProfileWithoutPasswordOmitsItAndOpenNetworksSayNone()
    {
        var noPassword = ParsePlist(IphoneProfile.Build(new WifiNetwork("Дом", null, IPAddress.Parse("192.168.1.5")), 8880));
        Assert.Null(PlistValue(noPassword, "Password"));
        Assert.Equal("Any", PlistValue(noPassword, "EncryptionType"));
        Assert.Equal("http://192.168.1.5:8880/proxy.pac", PlistValue(noPassword, "ProxyPACURL"));

        var open = ParsePlist(IphoneProfile.Build(new WifiNetwork("Кафе", null, IPAddress.Parse("10.0.0.2"), IsOpen: true), 8880));
        Assert.Equal("None", PlistValue(open, "EncryptionType"));
    }

    /// <summary>Первым показывается адрес в домашнем Wi-Fi: там телефон обычно и находится. Точка доступа ПК — последней.</summary>
    [Fact]
    public void HomeWifiAddressComesBeforeCableAndHotspot()
    {
        Assert.True(NetworkPolicy.Rank(isWindowsHotspot: false, isWireless: true) < NetworkPolicy.Rank(false, false));
        Assert.True(NetworkPolicy.Rank(false, false) < NetworkPolicy.Rank(true, true));
    }

    [WindowsPowerShellFact]
    public void ReadingCurrentWifiNeverThrows()
    {
        // На раннере CI нет Wi-Fi: ответ null, а не исключение.
        var wifi = WindowsWifi.Current();
        Assert.True(wifi is null || wifi.Ssid.Length > 0);
    }

    [Fact]
    public async Task ProfileIsServedOnlyWhileTheHotspotIsKnown()
    {
        WifiNetwork? wifi = null;
        var proxy = Proxy(new ShareProxyOptions { Port = 0, BindAddress = IPAddress.Loopback, Wifi = () => wifi });

        using (var phone = await Connect(proxy.Port))
        {
            await Send(phone, "GET /i HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 404", await ReadUntil(phone, "", toEnd: true));
        }

        wifi = new WifiNetwork("Zapret-PC", "secret123", IPAddress.Parse("192.168.137.1"));
        using (var phone = await Connect(proxy.Port))
        {
            await Send(phone, "GET /i HTTP/1.1\r\nHost: x\r\n\r\n");
            var response = await ReadUntil(phone, "", toEnd: true);
            Assert.StartsWith("HTTP/1.1 200", response);
            Assert.Contains("Content-Type: application/x-apple-aspen-config", response);
            Assert.Contains("<string>Zapret-PC</string>", response);
            Assert.Contains($"<string>http://192.168.137.1:{proxy.Port}/proxy.pac</string>", response);
        }
    }

    [Fact]
    public void ProbeDescribesIpv4PacketsAndSkipsTheRest()
    {
        var packet = new byte[40];
        packet[0] = 0x45; packet[8] = 63; packet[9] = 6;
        new byte[] { 192, 168, 137, 23 }.CopyTo(packet, 12);
        new byte[] { 142, 250, 74, 14 }.CopyTo(packet, 16);
        packet[22] = 0x01; packet[23] = 0xBB;
        var d = ForwardProbe.Describe(packet);
        Assert.Equal("192.168.137.23 → 142.250.74.14:443 tcp ttl 63", d!.Value.Text);
        Assert.Equal(IPAddress.Parse("142.250.74.14"), d.Value.Destination);

        packet[9] = 1; // ICMP
        Assert.Null(ForwardProbe.Describe(packet));
        Assert.Null(ForwardProbe.Describe(new byte[] { 0x60, 0, 0 }));
        Assert.Null(ForwardProbe.Describe(packet.AsSpan(0, 10)));
    }

    [Fact]
    public void ProbeVerdictsTellWhatToDoNext()
    {
        Assert.Contains("пришлите", ForwardProbe.Explain(new ForwardProbeResult(12, 0, [], null)));
        Assert.Contains("входящий", ForwardProbe.Explain(new ForwardProbeResult(0, 5, [], null)));
        Assert.Contains("остаётся прокси", ForwardProbe.Explain(new ForwardProbeResult(0, 0, [], null)));
        Assert.StartsWith("Проверка не удалась", ForwardProbe.Explain(ForwardProbeResult.Failed("x")));
    }

    public sealed class WinDivertFactAttribute : FactAttribute
    {
        public WinDivertFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ZS_ENGINE") is not { Length: > 0 })
                Skip = "нужны Windows и собранный движок с WinDivert (ZS_ENGINE)";
        }
    }

    /// <summary>
    /// На раннере нет точки доступа, так что пакетов не будет. Проверяется, что WinDivert открывает слой пересылки
    /// и входящий слой с нашими фильтрами (неверный фильтр дал бы ошибку открытия) и проверка сама заканчивается.
    /// </summary>
    [WinDivertFact]
    public async Task ProbeOpensTheForwardLayerAndStopsOnTime()
    {
        var dll = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("ZS_ENGINE"))!, "WinDivert.dll");
        var sw = Stopwatch.StartNew();
        var r = await ForwardProbe.RunAsync(dll, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Null(r.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }

    /// <summary>
    /// Фильтр перехвата SYN принимается драйвером, перехват останавливается по отмене сразу, а не через минуту.
    /// Пакетов на раннере не будет: подключение к своему адресу идёт через loopback, а его фильтр отсекает.
    /// </summary>
    [WinDivertFact]
    public async Task ReachSniffOpensWithTheSynFilterAndStopsWhenAsked()
    {
        var dll = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("ZS_ENGINE"))!, "WinDivert.dll");
        Assert.Null(ZapretSmart.Core.Engine.WinDivertFilter.Check(dll, ShareReachProbe.SynFilter(8880)));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var sw = Stopwatch.StartNew();
        Assert.Null(await ForwardProbe.SniffAsync(dll, ShareReachProbe.SynFilter(8880), TimeSpan.FromMinutes(5), _ => { }, stop.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), sw.Elapsed.ToString());
    }

    /// <summary>Состояние брандмауэра читается и не виснет; на раннере «блокировать все входящие» выключено.</summary>
    [WindowsPowerShellFact]
    public async Task FirewallStateIsReadWithoutHanging()
    {
        var sw = Stopwatch.StartNew();
        var state = await ShareReachProbe.ReadFirewallAsync(CancellationToken.None);
        Assert.False(state.BlocksAllInbound);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(45), sw.Elapsed.ToString());
    }

    public sealed class WindowsPowerShellFactAttribute : FactAttribute
    {
        public WindowsPowerShellFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "WinRT и брандмауэр Windows есть только в Windows";
        }
    }

    /// <summary>
    /// На раннере CI нет Wi-Fi, поэтому точку доступа не включить. Проверяется, что скрипт загружает WinRT,
    /// доходит до Windows и возвращает либо состояние, либо понятную ошибку, а не падает и не виснет.
    /// </summary>
    [WindowsPowerShellFact]
    public async Task HotspotScriptTalksToWindowsAndAnswers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zs-hotspot-" + Guid.NewGuid().ToString("N"));
        var state = await WindowsHotspot.RunAsync(HotspotAction.Status, dir, CancellationToken.None);
        Assert.True(state.Error is not null || state.Ssid is not null, state.ToString());
        Assert.DoesNotContain("AsTask", state.Error ?? "");
        Assert.DoesNotContain("Unable to find type", state.Error ?? "");
    }

    /// <summary>Правило брандмауэра ставится и снимается; путь к программе с пробелом доходит до netsh целиком.</summary>
    [WindowsPowerShellFact]
    public async Task FirewallRuleIsAddedAndRemoved()
    {
        using var serial = FirewallTestLock.Take();
        var program = @"C:\Program Files\Zapret Smart Test\ZapretSmart.exe";
        Assert.Null((await ShareFirewall.AllowAsync(program, 48880, CancellationToken.None)).Error);
        try
        {
            var shown = Netsh("advfirewall", "firewall", "show", "rule", $"name={ShareFirewall.RuleName}", "verbose");
            Assert.Contains("48880", shown);
            Assert.Contains(program, shown, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await ShareFirewall.RemoveAsync(CancellationToken.None);
        }
        Assert.DoesNotContain("48880", Netsh("advfirewall", "firewall", "show", "rule", $"name={ShareFirewall.RuleName}"));
    }

    /// <summary>
    /// Запрет, который Windows ставит после «Отмены» в своём окне, сильнее нашего разрешения: без его снятия телефон
    /// получает таймаут. Правила для других программ не трогаются.
    /// </summary>
    [WindowsPowerShellFact]
    public async Task FirewallRuleRemovesWindowsBlockForTheProgramOnly()
    {
        using var serial = FirewallTestLock.Take();
        var program = @"C:\Program Files\Zapret Smart Test\ZapretSmart.exe";
        var other = @"C:\Program Files\Zapret Smart Test\Other.exe";
        const string windowsBlock = "zs-test-zapretsmart.exe";
        const string otherBlock = "zs-test-other.exe";
        Netsh("advfirewall", "firewall", "add", "rule", $"name={windowsBlock}", "dir=in", "action=block", "protocol=TCP", $"program={program}", "profile=any");
        Netsh("advfirewall", "firewall", "add", "rule", $"name={otherBlock}", "dir=in", "action=block", "protocol=TCP", $"program={other}", "profile=any");
        try
        {
            var result = await ShareFirewall.AllowAsync(program, 48881, CancellationToken.None);
            Assert.Null(result.Error);
            Assert.Equal(1, result.RemovedProgramRules);
            Assert.DoesNotContain(windowsBlock, Netsh("advfirewall", "firewall", "show", "rule", $"name={windowsBlock}"));
            Assert.Contains(otherBlock, Netsh("advfirewall", "firewall", "show", "rule", $"name={otherBlock}"));
            Assert.Contains("48881", Netsh("advfirewall", "firewall", "show", "rule", $"name={ShareFirewall.RuleName}", "verbose"));
        }
        finally
        {
            await ShareFirewall.RemoveAsync(CancellationToken.None);
            Netsh("advfirewall", "firewall", "delete", "rule", $"name={windowsBlock}");
            Netsh("advfirewall", "firewall", "delete", "rule", $"name={otherBlock}");
        }
    }

    private static ProcessStartInfo Shell(string script) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", script } }
        : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script } };

    [Fact]
    public async Task ProcessRunnerReturnsCodeAndBothStreams()
    {
        var r = await ProcessRunner.RunAsync(Shell(OperatingSystem.IsWindows() ? "echo out& echo err 1>&2& exit 3" : "echo out; echo err 1>&2; exit 3"), TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Null(r.StartError);
        Assert.False(r.TimedOut);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("out", r.Output);
        Assert.Contains("err", r.Errors);
    }

    [Fact]
    public async Task ProcessRunnerKillsAHungProcessInsteadOfWaiting()
    {
        var sw = Stopwatch.StartNew();
        var r = await ProcessRunner.RunAsync(Shell(OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 >nul" : "sleep 60"), TimeSpan.FromMilliseconds(500), CancellationToken.None);
        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task ProcessRunnerReportsAMissingProgramInsteadOfThrowing()
    {
        var r = await ProcessRunner.RunAsync(new ProcessStartInfo("zs-no-such-program-" + Guid.NewGuid().ToString("N")), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(r.StartError);
        Assert.Contains("не запустился", r.StartError);
    }

    private static string Netsh(params string[] args)
    {
        var psi = new ProcessStartInfo("netsh") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
