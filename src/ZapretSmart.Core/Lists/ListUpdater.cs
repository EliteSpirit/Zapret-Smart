using System.Net;

namespace ZapretSmart.Core.Lists;

public sealed record ListSource(string Name, Uri Url, int MinEntries);

public sealed record ListSubscription(string Id, ListKind Kind, string Title, IReadOnlyList<ListSource> Sources);

public static class Subscriptions
{
    /// <summary>
    /// Re:filter — курируемый список, в нём есть и то, чего нет в реестре (YouTube, googlevideo).
    /// Полный реестр antifilter (32 МБ, около миллиона доменов, в основном казино) не используется.
    /// </summary>
    public static readonly ListSubscription BlockedDomains = new("blocked", ListKind.Domains, "Заблокированные домены",
    [
        new("Re:filter", new Uri("https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst"), 10_000),
        new("antifilter community", new Uri("https://community.antifilter.download/list/domains.lst"), 100),
    ]);

    public static readonly ListSubscription BlockedIps = new("blocked-ip", ListKind.Ips, "Заблокированные IP",
    [
        new("Re:filter", new Uri("https://github.com/1andrevich/Re-filter-lists/releases/latest/download/ipsum.lst"), 1_000),
        new("antifilter community", new Uri("https://community.antifilter.download/list/community.lst"), 100),
    ]);

    public static readonly IReadOnlyList<ListSubscription> All = [BlockedDomains, BlockedIps];

    public static bool IsSubscription(string id) => All.Any(s => s.Id == id);
}

public sealed record ListUpdateResult(
    ListSubscription Subscription,
    bool Updated,
    int Entries,
    IReadOnlyList<string> Notes,
    string? Error);

/// <summary>
/// Скачивает подписки и заменяет файлы атомарно. Файл не заменяется, если результат подозрителен:
/// все источники недоступны, записей меньше порога или меньше половины от прежнего. Пустой файл не пишется никогда.
/// </summary>
public sealed class ListUpdater(HttpClient http, string listsDir, string ipsetsDir)
{
    public const long MaxDownloadBytes = 8 * 1024 * 1024;
    public const int MaxEntries = 500_000;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public string PathFor(ListSubscription s) => Path.Combine(s.Kind == ListKind.Domains ? listsDir : ipsetsDir, s.Id + ".txt");

    public DateTime? LastUpdatedUtc(ListSubscription s) =>
        File.Exists(PathFor(s)) ? File.GetLastWriteTimeUtc(PathFor(s)) : null;

    public int CountEntries(ListSubscription s) =>
        File.Exists(PathFor(s)) ? File.ReadLines(PathFor(s)).Count(l => l.Length > 0) : 0;

    public bool IsStale(ListSubscription s, DateTime nowUtc) =>
        LastUpdatedUtc(s) is not { } t || nowUtc - t > MaxAge;

    public async Task<ListUpdateResult> UpdateAsync(ListSubscription sub, CancellationToken ct)
    {
        var notes = new List<string>();
        var merged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var src in sub.Sources)
        {
            string text;
            try
            {
                text = await DownloadAsync(src.Url, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                notes.Add($"{src.Name}: не скачан ({e.GetBaseException().Message})");
                continue;
            }

            var parsed = ListParser.Parse(text, sub.Kind);
            if (parsed.Entries.Count < src.MinEntries)
            {
                notes.Add($"{src.Name}: подозрительно мало записей ({parsed.Entries.Count}), пропущен");
                continue;
            }
            merged.UnionWith(parsed.Entries);
            var dropped = new List<string>();
            if (parsed.Invalid > 0) dropped.Add($"некорректных {parsed.Invalid}");
            if (parsed.TooBroad > 0) dropped.Add($"слишком широких подсетей {parsed.TooBroad}");
            if (parsed.Reserved > 0) dropped.Add($"служебных адресов {parsed.Reserved}");
            notes.Add($"{src.Name}: {parsed.Entries.Count}" + (dropped.Count > 0 ? $" (отброшено: {string.Join(", ", dropped)})" : ""));
        }

        var previous = CountEntries(sub);
        string? error = null;
        if (merged.Count == 0)
            error = "ни один источник не дал данных";
        else if (merged.Count > MaxEntries)
            error = $"слишком много записей ({merged.Count})";
        else if (previous > 0 && merged.Count < previous / 2)
            error = $"записей стало {merged.Count} вместо {previous} — похоже на обрезанную загрузку";
        if (error is not null)
            return new ListUpdateResult(sub, false, previous, notes, error + (previous > 0 ? "; оставлен прежний список" : ""));

        var path = PathFor(sub);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllLinesAsync(tmp, merged.Order(StringComparer.Ordinal), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        return new ListUpdateResult(sub, true, merged.Count, notes, null);
    }

    private async Task<string> DownloadAsync(Uri url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        if (resp.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new InvalidDataException("файл больше лимита");

        await using var body = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = await body.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > MaxDownloadBytes) throw new InvalidDataException("файл больше лимита");
            buffer.Write(chunk, 0, n);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
}
