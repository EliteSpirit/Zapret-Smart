using System.Text.RegularExpressions;
using ZapretSmart.Core.Search;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Community;

/// <summary>
/// Всё, что уходит в общую базу. Здесь нет доменов пользователя, hostlist, журналов и идентификаторов устройства.
/// Метрики допустимы только по стандартному набору целей: свой набор доменов — это отпечаток интересов человека.
/// </summary>
public sealed record CommunityReport(
    uint Asn,
    string Region,
    IReadOnlyList<string> Args,
    int Passed,
    int Total,
    int MedianLatencyMs,
    int SchemaVersion = 1);

public static partial class CommunityReports
{
    public static readonly IReadOnlyList<string> StandardTargets = ["www.youtube.com", "discord.com", "rutracker.org"];

    [GeneratedRegex(@"^[\p{L}][\p{L} .-]{0,63}$")]
    private static partial Regex RegionRe();

    /// <summary>
    /// Можно ли отправить отчёт по этим целям: стандартный набор или стандартный набор с CDN сервисов
    /// (SearchTargets.Default). Свой набор доменов — отпечаток интересов человека, его не отправляем.
    /// </summary>
    public static bool IsStandardTargets(IEnumerable<string> targets)
    {
        var set = targets.Select(SearchTargets.Normalize).Distinct().Order(StringComparer.Ordinal).ToList();
        return set.SequenceEqual(StandardTargets.Order(StringComparer.Ordinal))
            || set.SequenceEqual(SearchTargets.Default.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// null — отчёт не формируется: нет результата или цели не стандартные. Метрики считаются только по трём
    /// стандартным сайтам, даже если поиск шёл и по CDN: так отчёты разных версий сравнимы между собой.
    /// </summary>
    public static CommunityReport? FromSearch(SearchResult result, IEnumerable<string> targets, uint asn, string region)
    {
        if (result.Best is not { } best || !IsStandardTargets(targets)) return null;
        if (asn == 0 || !RegionRe().IsMatch(region)) return null;
        var total = result.Blocked.Count(StandardTargets.Contains);
        if (total == 0) return null;
        var passed = total - best.Failed.Count(StandardTargets.Contains);
        return new CommunityReport(asn, region, best.Candidate.Args, passed, total, (int)best.MedianLatency.TotalMilliseconds);
    }

    /// <summary>
    /// Отчёт из сети превращается в стратегию только через тот же белый список, что и всё остальное.
    /// Перед применением стратегию из базы всё равно нужно прогнать локальным тестером.
    /// </summary>
    public static Strategy? ToStrategy(CommunityReport report, IReadOnlyList<string> hostlists)
    {
        if (report.Args is null || report.Args.Count is 0 or > StrategyValidator.MaxArgsPerProfile) return null;
        // Из базы приходит TCP-часть; QUIC и голос Discord добавляются так же, как к стратегии из своего поиска.
        var s = UdpProfiles.Add(CandidateGenerator.ToStrategy(
            new Candidate("community", report.Args),
            id: "community-" + report.Asn,
            name: $"Сообщество: AS{report.Asn}",
            hostlists: hostlists,
            autoHostlist: false,
            description: $"Из общей базы: {report.Passed}/{report.Total} целей, {report.MedianLatencyMs} мс. Не проверена у вас. UDP: QUIC и голос Discord из готовых стратегий."),
            quic: true, voice: true, hostlists);
        return StrategyValidator.Validate(s).Count == 0 ? s : null;
    }
}
