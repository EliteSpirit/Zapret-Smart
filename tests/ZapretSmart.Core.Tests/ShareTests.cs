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
        var program = @"C:\Program Files\Zapret Smart Test\ZapretSmart.exe";
        Assert.Null(await ShareFirewall.AllowAsync(program, 48880, CancellationToken.None));
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
