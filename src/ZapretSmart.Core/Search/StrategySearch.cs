using System.Diagnostics;

namespace ZapretSmart.Core.Search;

public enum SearchPhase { Baseline, Explore, Refine, Verify, Done }

public sealed record CandidateScore(Candidate Candidate, IReadOnlyList<string> Failed, int Total, TimeSpan MedianLatency, string? Error)
{
    public int Passed => Total - Failed.Count;
    public bool IsFull => Total > 0 && Failed.Count == 0;
}

public sealed record SearchProgress(SearchPhase Phase, string Message, CandidateScore? Tested = null);

public sealed record SearchOptions
{
    public const string TargetsListId = "zs-search-targets";

    public required IReadOnlyList<string> Targets { get; init; }
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromMinutes(6);
    public int RefineSeeds { get; init; } = 2;
    public int VerifyRounds { get; init; } = 2;
    /// <summary>Сколько подряд неудачных запусков движка считать поломкой, а не свойством кандидата.</summary>
    public int MaxEngineFailuresInRow { get; init; } = 3;
}

public sealed record SearchResult(
    IReadOnlyList<string> Blocked,
    IReadOnlyList<string> NotBlocked,
    IReadOnlyList<CandidateScore> Ranking,
    CandidateScore? Best,
    bool BudgetExhausted)
{
    /// <summary>Цели, которые не открылись и с лучшим кандидатом: вероятно, блокировка по IP или подмена DNS.</summary>
    public IReadOnlyList<string> Unreachable => Best?.Failed ?? Blocked;
}

public sealed class SearchAbortedException(string message) : Exception(message);

public sealed class StrategySearch(IEngineHost engine, IProber prober, Func<TimeSpan>? clock = null)
{
    private readonly Func<TimeSpan> _clock = clock ?? StartStopwatch();

    private static Func<TimeSpan> StartStopwatch()
    {
        var sw = Stopwatch.StartNew();
        return () => sw.Elapsed;
    }

    public async Task<SearchResult> RunAsync(SearchOptions options, IProgress<SearchProgress>? progress, CancellationToken ct)
    {
        if (options.Targets.Count == 0)
            throw new ArgumentException("нужна хотя бы одна цель", nameof(options));

        var deadline = _clock() + options.TimeBudget;
        bool OutOfTime() => _clock() >= deadline;

        progress?.Report(new SearchProgress(SearchPhase.Baseline, "Проверка без обхода"));
        var baseline = await ProbeAllAsync(options.Targets, ct).ConfigureAwait(false);
        var blocked = baseline.Where(r => !r.Ok).Select(r => r.Domain).ToList();
        var notBlocked = baseline.Where(r => r.Ok).Select(r => r.Domain).ToList();
        if (blocked.Count == 0)
        {
            progress?.Report(new SearchProgress(SearchPhase.Done, "Все цели открываются без обхода"));
            return new SearchResult(blocked, notBlocked, [], null, false);
        }

        var tested = new Dictionary<string, CandidateScore>();
        var engineFailures = 0;

        async Task<CandidateScore?> TestAsync(Candidate c, SearchPhase phase)
        {
            if (tested.ContainsKey(c.Key)) return null;
            progress?.Report(new SearchProgress(phase, c.Label));
            var score = await ScoreAsync(c, blocked, ct).ConfigureAwait(false);
            tested[c.Key] = score;
            engineFailures = score.Error is null ? 0 : engineFailures + 1;
            if (engineFailures >= options.MaxEngineFailuresInRow)
                throw new SearchAbortedException("Движок не запускается: " + score.Error);
            progress?.Report(new SearchProgress(phase, c.Label, score));
            return score;
        }

        foreach (var c in CandidateGenerator.Explore())
        {
            if (OutOfTime() || tested.Values.Count(s => s.IsFull) >= options.RefineSeeds) break;
            await TestAsync(c, SearchPhase.Explore).ConfigureAwait(false);
        }

        foreach (var seed in Rank(tested.Values).Where(s => s.Passed > 0).Take(options.RefineSeeds).ToList())
        {
            foreach (var c in CandidateGenerator.Refine(seed.Candidate))
            {
                if (OutOfTime()) break;
                await TestAsync(c, SearchPhase.Refine).ConfigureAwait(false);
            }
        }

        var budgetExhausted = OutOfTime();
        CandidateScore? best = null;
        foreach (var contender in Rank(tested.Values).Where(s => s.Passed > 0).Take(3).ToList())
        {
            progress?.Report(new SearchProgress(SearchPhase.Verify, contender.Candidate.Label));
            var stable = true;
            for (var round = 0; round < options.VerifyRounds && stable; round++)
            {
                var again = await ScoreAsync(contender.Candidate, blocked, ct).ConfigureAwait(false);
                stable = again.Passed >= contender.Passed;
            }
            if (stable)
            {
                best = contender;
                break;
            }
        }

        var result = new SearchResult(blocked, notBlocked, Rank(tested.Values).ToList(), best, budgetExhausted);
        progress?.Report(new SearchProgress(SearchPhase.Done, best is null ? "Рабочая стратегия не найдена" : "Найдено: " + best.Candidate.Label));
        return result;
    }

    public static IEnumerable<CandidateScore> Rank(IEnumerable<CandidateScore> scores) =>
        scores.OrderByDescending(s => s.Total == 0 ? 0 : (double)s.Passed / s.Total)
            .ThenBy(s => s.MedianLatency)
            .ThenBy(s => s.Candidate.Args.Count);

    private async Task<CandidateScore> ScoreAsync(Candidate c, IReadOnlyList<string> targets, CancellationToken ct)
    {
        IAsyncDisposable session;
        try
        {
            session = await engine.StartAsync(CandidateGenerator.ToProbeStrategy(c, SearchOptions.TargetsListId), ct).ConfigureAwait(false);
        }
        catch (EngineStartException e)
        {
            return new CandidateScore(c, targets, targets.Count, TimeSpan.Zero, e.Message);
        }

        IReadOnlyList<ProbeResult> results;
        await using (session.ConfigureAwait(false))
            results = await ProbeAllAsync(targets, ct).ConfigureAwait(false);

        var ok = results.Where(r => r.Ok).Select(r => r.Elapsed).Order().ToList();
        var failed = results.Where(r => !r.Ok).Select(r => r.Domain).ToList();
        return new CandidateScore(c, failed, targets.Count, ok.Count == 0 ? TimeSpan.Zero : ok[ok.Count / 2], null);
    }

    private Task<ProbeResult[]> ProbeAllAsync(IReadOnlyList<string> targets, CancellationToken ct) =>
        Task.WhenAll(targets.Select(t => prober.ProbeAsync(t, ct)));
}
