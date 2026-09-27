using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Authentication;

namespace ZapretSmart.Core.Search;

/// <summary>
/// Проверка доступности: TLS-рукопожатие, HTTP-ответ и чтение тела до конца или до MinBytes.
/// Порог в байтах ловит блокировки, которые пропускают рукопожатие, но замораживают поток после ~16 КБ.
/// Системный прокси по умолчанию не используется: через прокси (часто его ставят VPN-клиенты) проверялся бы
/// прокси, а не обход DPI, и любой кандидат выглядел бы рабочим.
/// </summary>
public sealed class HttpsProber(TimeSpan timeout, int minBytes = 64 * 1024, bool useProxy = false) : IProber
{
    private static readonly ProductInfoHeaderValue UserAgent = new("Mozilla", "5.0");

    public async Task<ProbeResult> ProbeAsync(string domain, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long total = 0;
        using var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = timeout,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            UseProxy = useProxy,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{domain}/");
            req.Headers.UserAgent.Add(UserAgent);
            req.Headers.ConnectionClose = true;
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buf = new byte[16 * 1024];
            int n;
            while (total < minBytes && (n = await body.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
                total += n;
            return new ProbeResult(domain, true, sw.Elapsed, total, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(domain, false, sw.Elapsed, total, total > 0 ? $"поток встал после {total} байт" : "таймаут");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or AuthenticationException)
        {
            return new ProbeResult(domain, false, sw.Elapsed, total, e.GetBaseException().Message);
        }
    }
}
