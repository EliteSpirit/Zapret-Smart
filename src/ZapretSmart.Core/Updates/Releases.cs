using System.Net.Http.Headers;
using System.Text.Json;

namespace ZapretSmart.Core.Updates;

/// <summary>Номер версии вида 0.3.0 или 0.3.1-rc.1. Пререлиз младше выпуска с теми же цифрами.</summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string? Label) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = new AppVersion(0, 0, 0, null);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = s.IndexOf('-');
        var label = dash < 0 ? null : s[(dash + 1)..];
        var parts = (dash < 0 ? s : s[..dash]).Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b) || !int.TryParse(parts[2], out var c)) return false;
        if (label is { Length: 0 }) return false;
        version = new AppVersion(a, b, c, label);
        return true;
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var d = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (d != 0) return d;
        if (Label == other.Label) return 0;
        if (Label is null) return 1;
        if (other.Label is null) return -1;
        return string.CompareOrdinal(Label, other.Label);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Label is null ? "" : "-" + Label);
}

/// <param name="ZipUrl">Архив сборки для Windows.</param>
/// <param name="SumsUrl">SHA256SUMS.txt того же релиза.</param>
public sealed record ReleaseInfo(AppVersion Version, string Tag, bool IsPrerelease, DateTimeOffset PublishedAt, string ZipName, string ZipUrl, string SumsUrl);

public static class Releases
{
    public const string Repository = "EliteSpirit/Zapret-Smart";

    /// <summary>Релизы, у которых есть архив для Windows и файл с контрольными суммами, от новых к старым.</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        var list = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString();
            if (!AppVersion.TryParse(tag, out var version)) continue;
            var zipName = $"ZapretSmart-{version}-win-x64.zip";
            string? zip = null, sums = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name == zipName) zip = url;
                else if (name == "SHA256SUMS.txt") sums = url;
            }
            // Без контрольных сумм не ставим: нечем проверить, что скачалось именно то, что выложено.
            if (zip is null || sums is null || !IsGitHubDownload(zip) || !IsGitHubDownload(sums)) continue;
            var published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetDateTimeOffset()
                : DateTimeOffset.MinValue;
            list.Add(new ReleaseInfo(version, tag!, r.GetProperty("prerelease").GetBoolean(), published, zipName, zip, sums));
        }
        return list.OrderByDescending(x => x.Version).ToList();
    }

    /// <summary>Скачиваем только с github.com этого репозитория: ссылка из ответа API не должна увести на чужой сервер.</summary>
    public static bool IsGitHubDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "github.com"
        && u.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.Ordinal);

    public static HttpClient CreateHttpClient(string currentVersion)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretSmart", currentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public static async Task<IReadOnlyList<ReleaseInfo>> FetchAsync(HttpClient http, CancellationToken ct) =>
        Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=50", ct));
}
