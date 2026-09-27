using ZapretSmart.Core.Search;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Watchdog;

public sealed record StrategyScore(Strategy Strategy, int Passed, int Total, string? Error);

/// <summary>Прогоняет готовые стратегии (со своими списками) по проверочным сайтам, по очереди, через движок.</summary>
public sealed class StrategyRanker(IEngineHost engine, IProber prober)
{
    public async Task<IReadOnlyList<StrategyScore>> RankAsync(IReadOnlyList<Strategy> strategies, IReadOnlyList<string> targets, CancellationToken ct)
    {
        var scores = new List<StrategyScore>();
        foreach (var s in strategies)
        {
            ct.ThrowIfCancellationRequested();
            IAsyncDisposable session;
            try
            {
                session = await engine.StartAsync(s, ct).ConfigureAwait(false);
            }
            catch (EngineStartException e)
            {
                scores.Add(new StrategyScore(s, 0, targets.Count, e.Message));
                continue;
            }

            ProbeResult[] results;
            await using (session.ConfigureAwait(false))
                results = await Task.WhenAll(targets.Select(t => prober.ProbeAsync(t, ct))).ConfigureAwait(false);
            scores.Add(new StrategyScore(s, results.Count(r => r.Ok), targets.Count, null));
        }
        // Стабильная сортировка: при равенстве выигрывает стоящая раньше (текущая стратегия идёт первой).
        return scores.OrderByDescending(x => x.Passed).ToList();
    }

    public static async Task<int> CheckAsync(IProber prober, IReadOnlyList<string> targets, CancellationToken ct) =>
        (await Task.WhenAll(targets.Select(t => prober.ProbeAsync(t, ct))).ConfigureAwait(false)).Count(r => r.Ok);
}
