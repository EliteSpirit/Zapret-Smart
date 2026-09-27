using System.Diagnostics;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Search;
using ZapretSmart.Core.Strategies;
using ZapretSmart.Core.Watchdog;

namespace ZapretSmart.Core.Tests;

public class WatchdogTests
{
    private static readonly DateTime T0 = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static HealthTracker Tracker() => new(failuresBeforeSwitch: 2, switchCooldown: TimeSpan.FromMinutes(30));

    [Fact]
    public void FirstCheckSetsBaseline()
    {
        var t = Tracker();
        Assert.Equal(HealthVerdict.Baseline, t.Observe(2, T0));
        Assert.Equal(2, t.Baseline);
        Assert.Equal(HealthVerdict.Healthy, t.Observe(2, T0.AddMinutes(5)));
        Assert.Equal(HealthVerdict.Healthy, t.Observe(3, T0.AddMinutes(10)));
    }

    [Fact]
    public void SwitchesOnlyAfterConsecutiveDrops()
    {
        var t = Tracker();
        t.Observe(3, T0);
        Assert.Equal(HealthVerdict.Degraded, t.Observe(1, T0.AddMinutes(5)));
        Assert.Equal(HealthVerdict.Healthy, t.Observe(3, T0.AddMinutes(10)));
        Assert.Equal(HealthVerdict.Degraded, t.Observe(1, T0.AddMinutes(15)));
        Assert.Equal(HealthVerdict.Switch, t.Observe(0, T0.AddMinutes(20)));
    }

    [Fact]
    public void IpBlockedSiteDoesNotCountAsBreakage()
    {
        // Один из трёх сайтов заблокирован по IP и не открывался никогда: 2 из 3 — это норма для этой стратегии.
        var t = Tracker();
        t.Observe(2, T0);
        for (var i = 1; i <= 5; i++)
            Assert.Equal(HealthVerdict.Healthy, t.Observe(2, T0.AddMinutes(5 * i)));
    }

    [Fact]
    public void NeverWorkedDoesNotSwitchBlindly()
    {
        var t = Tracker();
        Assert.Equal(HealthVerdict.NeverWorked, t.Observe(0, T0));
        Assert.Equal(HealthVerdict.NeverWorked, t.Observe(0, T0.AddMinutes(5)));
        Assert.Equal(HealthVerdict.NeverWorked, t.Observe(0, T0.AddMinutes(10)));
    }

    [Fact]
    public void CooldownPreventsFlapping()
    {
        var t = Tracker();
        t.Observe(3, T0);
        t.Observe(0, T0.AddMinutes(5));
        Assert.Equal(HealthVerdict.Switch, t.Observe(0, T0.AddMinutes(10)));
        t.Restarted(3, T0.AddMinutes(11), switched: true);

        t.Observe(0, T0.AddMinutes(15));
        Assert.Equal(HealthVerdict.Cooldown, t.Observe(0, T0.AddMinutes(20)));
        t.Observe(0, T0.AddMinutes(45));
        Assert.Equal(HealthVerdict.Switch, t.Observe(0, T0.AddMinutes(50)));
    }

    [Fact]
    public void RestartWithoutSwitchKeepsNoCooldown()
    {
        var t = Tracker();
        t.Observe(3, T0);
        t.Restarted(2, T0.AddMinutes(1), switched: false);
        t.Observe(1, T0.AddMinutes(5));
        Assert.Equal(HealthVerdict.Switch, t.Observe(1, T0.AddMinutes(10)));
    }

    private sealed class FakeNet(Func<string, string, bool> opens) : IEngineHost, IProber
    {
        private string? _active;
        public List<string> Started { get; } = [];
        public string? FailToStart { get; init; }

        public Task<IAsyncDisposable> StartAsync(Strategy strategy, CancellationToken ct)
        {
            if (strategy.Id == FailToStart) throw new EngineStartException("нет прав");
            _active = strategy.Id;
            Started.Add(strategy.Id);
            return Task.FromResult<IAsyncDisposable>(new Stop(() => _active = null));
        }

        public Task<ProbeResult> ProbeAsync(string domain, CancellationToken ct) =>
            Task.FromResult(new ProbeResult(domain, _active is not null && opens(_active, domain), TimeSpan.Zero, 0, null));

        private sealed class Stop(Action a) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                a();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static Strategy S(string id) => new()
    {
        Id = id,
        Name = id,
        Intercept = new Intercept { Tcp = "443" },
        Profiles = [new Profile { Args = ["dpi-desync=multisplit"] }],
    };

    [Fact]
    public async Task RankerPicksStrategyOpeningMostTargets()
    {
        var net = new FakeNet((s, d) => s == "b" || (s == "a" && d == "x.com"));
        var ranked = await new StrategyRanker(net, net).RankAsync([S("a"), S("b"), S("c")], ["x.com", "y.com"], CancellationToken.None);
        Assert.Equal(["b", "a", "c"], ranked.Select(r => r.Strategy.Id));
        Assert.Equal(2, ranked[0].Passed);
        Assert.Equal(["a", "b", "c"], net.Started);
    }

    [Fact]
    public async Task RankerKeepsCurrentOnTie()
    {
        var net = new FakeNet((_, _) => true);
        var ranked = await new StrategyRanker(net, net).RankAsync([S("current"), S("other")], ["x.com"], CancellationToken.None);
        Assert.Equal("current", ranked[0].Strategy.Id);
    }

    [Fact]
    public async Task RankerSurvivesEngineThatCannotStart()
    {
        var net = new FakeNet((_, _) => true) { FailToStart = "a" };
        var ranked = await new StrategyRanker(net, net).RankAsync([S("a"), S("b")], ["x.com"], CancellationToken.None);
        Assert.Equal("b", ranked[0].Strategy.Id);
        Assert.Equal("нет прав", ranked[1].Error);
    }

    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Job Object есть только в Windows";
        }
    }

    [WindowsFact]
    public void ClosingJobKillsAssignedProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var p = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var job = new KillOnCloseJob();
        job.Assign(p);
        Assert.False(p.HasExited);
        job.Dispose();
        Assert.True(p.WaitForExit(10_000), "процесс пережил закрытие задания");
    }
}
