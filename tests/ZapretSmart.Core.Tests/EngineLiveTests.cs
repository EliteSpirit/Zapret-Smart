using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Search;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Tests;

/// <summary>
/// Настоящий winws с WinDivert: нужны Windows и права администратора. Включается переменной ZS_LIVE=1
/// вместе с ZS_ENGINE, указывающим на winws.exe (так настроен Windows-джоб CI).
/// </summary>
public sealed class EngineLiveTests : IDisposable
{
    private static readonly string? Engine = Environment.GetEnvironmentVariable("ZS_ENGINE");
    private readonly string _lists = Path.Combine(Path.GetTempPath(), "zs-live-" + Guid.NewGuid().ToString("N"));

    public sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ZS_LIVE") != "1" || string.IsNullOrEmpty(Engine))
                Skip = "ZS_LIVE=1 и ZS_ENGINE=winws.exe не заданы";
        }
    }

    public EngineLiveTests()
    {
        Directory.CreateDirectory(_lists);
        File.WriteAllLines(Path.Combine(_lists, SearchOptions.TargetsListId + ".txt"), ["example.com", "www.google.com"]);
    }

    private WinwsEngineHost Host(EngineScope scope = EngineScope.Pc) => new(
        Engine!,
        new EngineLayout(Path.Combine(Path.GetDirectoryName(Engine!)!, "fake"), _lists, _lists),
        TimeSpan.FromSeconds(20),
        scope);

    private static readonly Candidate PlainSplit = new("multisplit", ["dpi-desync=multisplit", "dpi-desync-split-pos=1,midsld"]);

    [LiveFact]
    public async Task EngineStartsAndTrafficStillFlowsThroughDesync()
    {
        await using (await Host().StartAsync(CandidateGenerator.ToProbeStrategy(PlainSplit, SearchOptions.TargetsListId), CancellationToken.None))
        {
            var r = await new HttpsProber(TimeSpan.FromSeconds(15)).ProbeAsync("www.google.com", CancellationToken.None);
            Assert.True(r.Ok, r.Error);
        }
    }

    [LiveFact]
    public async Task SecondCopyWithSameFilterIsReportedAsStartFailure()
    {
        var s = CandidateGenerator.ToProbeStrategy(PlainSplit, SearchOptions.TargetsListId);
        await using (await Host().StartAsync(s, CancellationToken.None))
        {
            var e = await Assert.ThrowsAsync<EngineStartException>(() => Host().StartAsync(s, CancellationToken.None));
            Assert.Contains("already running", e.Message);
        }
    }

    /// <summary>
    /// Основной обход и раздача — два независимых движка с одинаковым --wf-tcp. Без хэша --wf-lport в имени
    /// мьютекса второй не запустился бы («already running»). Трафик ПК при этом идёт через обход как обычно.
    /// </summary>
    [LiveFact]
    public async Task PcAndShareEnginesRunSideBySide()
    {
        var s = CandidateGenerator.ToProbeStrategy(PlainSplit, SearchOptions.TargetsListId);
        await using (await Host().StartAsync(s, CancellationToken.None))
        await using (await Host(EngineScope.Share).StartAsync(s, CancellationToken.None))
        {
            var r = await new HttpsProber(TimeSpan.FromSeconds(15)).ProbeAsync("www.google.com", CancellationToken.None);
            Assert.True(r.Ok, r.Error);
        }
    }

    [LiveFact]
    public async Task EngineIsReleasedAfterSession()
    {
        var s = CandidateGenerator.ToProbeStrategy(PlainSplit, SearchOptions.TargetsListId);
        for (var i = 0; i < 3; i++)
            await using (await Host().StartAsync(s, CancellationToken.None)) { }
    }

    [LiveFact]
    public async Task HostDisposeReleasesWinDivertForNextRun()
    {
        var s = CandidateGenerator.ToProbeStrategy(PlainSplit, SearchOptions.TargetsListId);
        var host = Host();
        await host.StartAsync(s, CancellationToken.None);
        host.Dispose();
        await using (await Host().StartAsync(s, CancellationToken.None)) { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_lists)) Directory.Delete(_lists, recursive: true);
    }
}
