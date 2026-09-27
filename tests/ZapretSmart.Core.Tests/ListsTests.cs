using System.Net;
using ZapretSmart.Core.Lists;

namespace ZapretSmart.Core.Tests;

public sealed class ListsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zs-lists-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DomainsAreNormalizedAndValidated()
    {
        var p = ListParser.ParseDomains("""
            # комментарий
            YouTube.com
            www.youtube.com.
            xn--80ak6aa92e.xn--p1ai
            discord.com   # хвостовой комментарий
            youtube.com
            144.31.236.132
            localhost
            ../etc/passwd
            bad domain.com
            *.wild.com

            """);
        Assert.Equal(["discord.com", "www.youtube.com", "xn--80ak6aa92e.xn--p1ai", "youtube.com"], p.Entries);
        Assert.Equal(5, p.Invalid);
    }

    [Fact]
    public void IpsDropBroadAndReservedRanges()
    {
        var p = ListParser.ParseIps("""
            104.16.0.0/12
            142.250.0.0/15
            172.217.0.0/16
            198.23.57.168/32
            5.6.7.8
            5.6.7.9/24
            10.1.2.0/24
            192.168.0.0/16
            127.0.0.1
            192.0.2.0/32
            2a00:1450::/32
            2a00::/16
            fe80::1
            1.2.3
            1.2.3.4/33
            not-an-ip
            """);
        Assert.Equal(["172.217.0.0/16", "198.23.57.168/32", "2a00:1450::/32", "5.6.7.0/24", "5.6.7.8/32"], p.Entries);
        Assert.Equal(3, p.TooBroad);
        Assert.Equal(5, p.Reserved);
        Assert.Equal(3, p.Invalid);
    }

    private sealed class FakeHttp(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request.RequestUri!));
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string Domains(int n, string prefix = "d") => string.Join('\n', Enumerable.Range(0, n).Select(i => $"{prefix}{i}.example.org"));

    private (ListUpdater Updater, ListSubscription Sub) Setup(Func<Uri, HttpResponseMessage> respond, int minA = 10, int minB = 5)
    {
        var sub = new ListSubscription("blocked", ListKind.Domains, "t",
        [
            new ListSource("A", new Uri("https://a.test/list"), minA),
            new ListSource("B", new Uri("https://b.test/list"), minB),
        ]);
        return (new ListUpdater(new HttpClient(new FakeHttp(respond)), Path.Combine(_dir, "lists"), Path.Combine(_dir, "ipsets")), sub);
    }

    [Fact]
    public async Task MergesSourcesAndWritesAtomically()
    {
        var (u, sub) = Setup(url => Ok(url.Host == "a.test" ? Domains(20) : Domains(10, "x") + "\nd0.example.org"));
        var r = await u.UpdateAsync(sub, CancellationToken.None);

        Assert.True(r.Updated, r.Error);
        Assert.Equal(30, r.Entries);
        Assert.Equal(30, File.ReadAllLines(u.PathFor(sub)).Length);
        Assert.False(File.Exists(u.PathFor(sub) + ".tmp"));
        Assert.StartsWith(Path.Combine(_dir, "lists"), u.PathFor(sub));
    }

    [Fact]
    public async Task OneSourceDownStillUpdatesFromOther()
    {
        var (u, sub) = Setup(url => url.Host == "a.test" ? Ok(Domains(20)) : new HttpResponseMessage(HttpStatusCode.BadGateway));
        var r = await u.UpdateAsync(sub, CancellationToken.None);
        Assert.True(r.Updated);
        Assert.Equal(20, r.Entries);
        Assert.Contains(r.Notes, n => n.StartsWith("B: не скачан", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllSourcesDownKeepsOldListAndNeverWritesEmpty()
    {
        var (u, sub) = Setup(_ => Ok(Domains(20)));
        await u.UpdateAsync(sub, CancellationToken.None);

        var (u2, _) = Setup(_ => throw new HttpRequestException("нет сети"));
        var r = await u2.UpdateAsync(sub, CancellationToken.None);

        Assert.False(r.Updated);
        Assert.Contains("оставлен прежний", r.Error);
        Assert.Equal(20, File.ReadAllLines(u.PathFor(sub)).Length);
    }

    [Fact]
    public async Task FirstDownloadFailingWritesNothing()
    {
        var (u, sub) = Setup(_ => Ok("<html>captive portal</html>"));
        var r = await u.UpdateAsync(sub, CancellationToken.None);
        Assert.False(r.Updated);
        Assert.False(File.Exists(u.PathFor(sub)));
    }

    [Fact]
    public async Task TruncatedDownloadDoesNotReplaceList()
    {
        var (u, sub) = Setup(_ => Ok(Domains(100)), minA: 10, minB: 10);
        await u.UpdateAsync(sub, CancellationToken.None);

        var (u2, _) = Setup(url => url.Host == "a.test" ? Ok(Domains(40)) : new HttpResponseMessage(HttpStatusCode.NotFound), minA: 10, minB: 10);
        var r = await u2.UpdateAsync(sub, CancellationToken.None);

        Assert.False(r.Updated);
        Assert.Contains("обрезанную", r.Error);
        Assert.Equal(100, File.ReadAllLines(u.PathFor(sub)).Length);
    }

    [Fact]
    public async Task OversizedDownloadIsRejected()
    {
        var huge = new string('a', (int)ListUpdater.MaxDownloadBytes + 10);
        var (u, sub) = Setup(url => url.Host == "a.test" ? Ok(huge) : Ok(Domains(10, "x")));
        var r = await u.UpdateAsync(sub, CancellationToken.None);
        Assert.True(r.Updated);
        Assert.Equal(10, r.Entries);
        Assert.Contains(r.Notes, n => n.Contains("лимита"));
    }

    [Fact]
    public async Task UserCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var (u, sub) = Setup(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => u.UpdateAsync(sub, cts.Token));
    }

    [Fact]
    public async Task StalenessFollowsFileAge()
    {
        var (u, sub) = Setup(_ => Ok(Domains(20)));
        Assert.True(u.IsStale(sub, DateTime.UtcNow));
        await u.UpdateAsync(sub, CancellationToken.None);
        Assert.False(u.IsStale(sub, DateTime.UtcNow));
        Assert.True(u.IsStale(sub, DateTime.UtcNow + ListUpdater.MaxAge + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void SubscriptionsPointToHttps()
    {
        Assert.All(Subscriptions.All.SelectMany(s => s.Sources), s => Assert.Equal("https", s.Url.Scheme));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
