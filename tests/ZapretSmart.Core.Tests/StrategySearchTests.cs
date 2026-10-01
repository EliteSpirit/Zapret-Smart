using ZapretSmart.Core.Search;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Tests;

public class StrategySearchTests
{
    /// <summary>Эмуляция провайдера: домен открывается, если правило пропускает активную стратегию.</summary>
    private sealed class FakeNetwork : IEngineHost, IProber
    {
        private readonly Dictionary<string, Func<Candidate?, bool>> _rules;
        private Candidate? _active;

        public FakeNetwork(Dictionary<string, Func<Candidate?, bool>> rules) => _rules = rules;

        public int Starts { get; private set; }
        public TimeSpan Now { get; private set; }
        public TimeSpan CostPerStart { get; init; } = TimeSpan.FromSeconds(3);
        public Func<Candidate, int, bool>? FailStart { get; init; }
        public Func<Candidate, int, bool>? Flaky { get; init; }
        public List<string> Log { get; } = [];

        public Task<IAsyncDisposable> StartAsync(Strategy strategy, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Empty(StrategyValidator.Validate(strategy));
            var args = strategy.Profiles.Single().Args.Skip(1).ToList();
            var c = new Candidate("", args);
            Starts++;
            Now += CostPerStart;
            if (FailStart?.Invoke(c, Starts) == true)
                throw new EngineStartException("нет прав администратора");
            _active = c;
            // Нормализуем сами, а не через Candidate.Key: тест проверяет именно дедупликацию по ключу.
            Log.Add(string.Join(' ', args.Order(StringComparer.Ordinal)));
            return Task.FromResult<IAsyncDisposable>(new Stop(() => _active = null));
        }

        public Task<ProbeResult> ProbeAsync(string domain, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var ok = _rules[domain](_active);
            if (ok && _active is not null && Flaky?.Invoke(_active, Log.Count(k => k == string.Join(' ', _active.Args.Order(StringComparer.Ordinal)))) == true) ok = false;
            return Task.FromResult(new ProbeResult(domain, ok, TimeSpan.FromMilliseconds(ok ? 100 + _active?.Args.Count ?? 0 : 0), 0, ok ? null : "таймаут"));
        }

        private sealed class Stop(Action a) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                a();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static bool Is(Candidate? c, string option, string value) => c?.Get(option) == value;

    private static Task<SearchResult> Run(FakeNetwork net, TimeSpan? budget = null, params string[] targets) =>
        new StrategySearch(net, net, () => net.Now).RunAsync(
            new SearchOptions { Targets = targets, TimeBudget = budget ?? TimeSpan.FromHours(1) }, null, CancellationToken.None);

    [Fact]
    public async Task NothingBlockedMeansNoEngineRuns()
    {
        var net = new FakeNetwork(new() { ["a.com"] = _ => true });
        var r = await Run(net, null, "a.com");
        Assert.Equal(["a.com"], r.NotBlocked);
        Assert.Null(r.Best);
        Assert.Equal(0, net.Starts);
    }

    [Fact]
    public async Task FindsTheOnlyWorkingTechnique()
    {
        static bool Dpi(Candidate? c) => Is(c, "dpi-desync", "fake,multidisorder") && Is(c, "dpi-desync-fooling", "datanoack");
        var net = new FakeNetwork(new() { ["yt.com"] = Dpi, ["dc.com"] = Dpi, ["ok.com"] = _ => true });

        var r = await Run(net, null, "yt.com", "dc.com", "ok.com");

        Assert.Equal(["yt.com", "dc.com"], r.Blocked);
        Assert.Equal(["ok.com"], r.NotBlocked);
        Assert.NotNull(r.Best);
        Assert.True(r.Best!.IsFull);
        Assert.True(Dpi(r.Best.Candidate));
        Assert.Empty(r.Unreachable);
    }

    [Fact]
    public async Task PrefersFasterCandidateAmongFullyWorking()
    {
        static bool Dpi(Candidate? c) => c is not null && (c.Get("dpi-desync")?.Contains("multisplit") ?? false);
        var net = new FakeNetwork(new() { ["a.com"] = Dpi });

        var r = await Run(net, null, "a.com");

        var fullOnes = r.Ranking.Where(s => s.IsFull).ToList();
        Assert.True(fullOnes.Count > 1);
        Assert.Equal(fullOnes.Min(s => s.MedianLatency), r.Best!.MedianLatency);
    }

    [Fact]
    public async Task IpBlockedTargetIsReportedAsUnreachable()
    {
        static bool Dpi(Candidate? c) => Is(c, "dpi-desync", "multisplit");
        var net = new FakeNetwork(new() { ["yt.com"] = Dpi, ["ipblocked.com"] = _ => false });

        var r = await Run(net, null, "yt.com", "ipblocked.com");

        Assert.NotNull(r.Best);
        Assert.False(r.Best!.IsFull);
        Assert.Equal(["ipblocked.com"], r.Unreachable);
    }

    [Fact]
    public async Task NothingWorksGivesNoBestAndAllUnreachable()
    {
        var net = new FakeNetwork(new() { ["a.com"] = _ => false });
        var r = await Run(net, null, "a.com");
        Assert.Null(r.Best);
        Assert.Equal(["a.com"], r.Unreachable);
        Assert.Equal(CandidateGenerator.Explore().Count, r.Ranking.Count);
    }

    [Fact]
    public async Task FlakyCandidateLosesVerification()
    {
        // Первый кандидат короче (значит, быстрее в эмуляции) всех рабочих, но срабатывает только при первом запуске.
        var first = CandidateGenerator.Explore()[0];
        static bool Dpi(Candidate? c) => c is not null &&
            (c.Key == CandidateGenerator.Explore()[0].Key || (Is(c, "dpi-desync", "fake,multidisorder") && Is(c, "dpi-desync-fooling", "badseq")));
        var net = new FakeNetwork(new() { ["a.com"] = Dpi })
        {
            Flaky = (c, runs) => c.Key == first.Key && runs > 1,
        };

        var r = await Run(net, null, "a.com");

        Assert.Equal(first.Key, r.Ranking[0].Candidate.Key);
        Assert.NotNull(r.Best);
        Assert.NotEqual(first.Key, r.Best!.Candidate.Key);
        Assert.True(Dpi(r.Best.Candidate));
    }

    [Fact]
    public async Task RespectsTimeBudget()
    {
        var net = new FakeNetwork(new() { ["a.com"] = _ => false }) { CostPerStart = TimeSpan.FromSeconds(10) };
        var r = await Run(net, TimeSpan.FromSeconds(35), "a.com");
        Assert.True(r.BudgetExhausted);
        Assert.Equal(4, r.Ranking.Count);
    }

    [Fact]
    public async Task BrokenEngineAbortsInsteadOfBurningBudget()
    {
        var net = new FakeNetwork(new() { ["a.com"] = _ => false }) { FailStart = (_, _) => true };
        var e = await Assert.ThrowsAsync<SearchAbortedException>(() => Run(net, null, "a.com"));
        Assert.Contains("нет прав администратора", e.Message);
        Assert.Equal(3, net.Starts);
    }

    [Fact]
    public async Task CancellationStopsSearch()
    {
        var net = new FakeNetwork(new() { ["a.com"] = _ => false });
        using var cts = new CancellationTokenSource();
        var search = new StrategySearch(net, net, () => net.Now);
        var progress = new SyncProgress(p => { if (p.Tested is not null) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            search.RunAsync(new SearchOptions { Targets = ["a.com"] }, progress, cts.Token));
        Assert.Equal(1, net.Starts);
    }

    [Fact]
    public async Task UnreachableMeansNoCandidateOpenedIt()
    {
        // A открывает yt и dc, B открывает только rt: rt не «недоступен по IP», его открыл B.
        static bool A(Candidate? c) => Is(c, "dpi-desync", "multisplit");
        static bool B(Candidate? c) => Is(c, "dpi-desync", "multidisorder");
        var net = new FakeNetwork(new() { ["yt.com"] = A, ["dc.com"] = A, ["rt.org"] = B, ["ip.com"] = _ => false });

        var r = await Run(net, null, "yt.com", "dc.com", "rt.org", "ip.com");

        Assert.True(A(r.Best!.Candidate));
        Assert.Equal(["rt.org"], r.MissedByBest);
        Assert.Equal(["ip.com"], r.Unreachable);
    }

    [Fact]
    public async Task SameArgumentSetIsNeverTestedTwiceExceptVerification()
    {
        static bool Dpi(Candidate? c) => Is(c, "dpi-desync", "fake,multidisorder") && Is(c, "dpi-desync-fooling", "badseq");
        var net = new FakeNetwork(new() { ["a.com"] = Dpi });

        var r = await Run(net, null, "a.com");

        var repeated = net.Log.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(repeated.Count <= 1, "повторно проверены: " + string.Join(" | ", repeated));
        if (repeated.Count == 1) Assert.Equal(string.Join(' ', r.Best!.Candidate.Args.Order(StringComparer.Ordinal)), repeated[0]);
    }

    [Fact]
    public void KeyIgnoresArgumentOrder()
    {
        Assert.Equal(new Candidate("a", ["x=1", "y=2"]).Key, new Candidate("b", ["y=2", "x=1"]).Key);
    }

    [Theory]
    [InlineData("youtube.com", "youtube.com", true)]
    [InlineData("www.youtube.com", "youtube.com", true)]
    [InlineData("x.com", "twitter.com", false)]
    [InlineData("evilyoutube.com", "youtube.com", false)]
    public void RedirectsAreFollowedOnlyWithinTarget(string host, string domain, bool same)
    {
        Assert.Equal(same, HttpsProber.IsSameSite(host, domain));
    }

    /// <summary>
    /// Цели по умолчанию: сайты вместе с их CDN. Стратегия, найденная по одной главной странице, оставляла YouTube
    /// без видео и картинок, а Discord без вложений и чата.
    /// </summary>
    [Fact]
    public void DefaultTargetsCoverServiceCdns()
    {
        var all = SearchTargets.Default;
        foreach (var cdn in new[] { "redirector.googlevideo.com", "yt3.ggpht.com", "gateway.discord.gg", "media.discordapp.net", "cdn.discordapp.com" })
            Assert.Contains(all, t => SearchTargets.Host(t) == cdn);
        Assert.Contains("i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg", all);
        foreach (var standard in ZapretSmart.Core.Community.CommunityReports.StandardTargets) Assert.Contains(standard, all);
        Assert.All(all, t => Assert.True(SearchTargets.IsValid(t), t));
    }

    [Theory]
    [InlineData("discord.com", "discord.com", "/")]
    [InlineData("I.YTIMG.com/vi/dQw4w9WgXcQ/maxresdefault.jpg", "i.ytimg.com", "/vi/dQw4w9WgXcQ/maxresdefault.jpg")]
    public void TargetsSplitIntoHostAndCaseSensitivePath(string target, string host, string path)
    {
        Assert.Equal(host, SearchTargets.Host(target));
        Assert.Equal(path, SearchTargets.PathOf(target));
        // Путь не переводится в нижний регистр: у обложки YouTube иначе был бы 404 и ложная «блокировка».
        Assert.Equal($"https://{host}{path}", HttpsProber.UrlFor(target).AbsoluteUri);
        Assert.EndsWith(path == "/" ? host : path, SearchTargets.Normalize(target));
    }

    [Theory]
    [InlineData("i.ytimg.com/vi/x.jpg?sig=1", false)]
    [InlineData("i.ytimg.com/../etc/passwd", false)]
    [InlineData("i.ytimg.com/a b.jpg", false)]
    [InlineData("not a domain", false)]
    [InlineData("cdn.discordapp.com/embed/avatars/0.png", true)]
    [InlineData("rutracker.org", true)]
    public void OnlyPlainDomainsAndSafeFilePathsAreAccepted(string target, bool valid) =>
        Assert.Equal(valid, SearchTargets.IsValid(target));

    [Fact]
    public void HostlistGetsDomainsOnlyAndResultsShowThemShort()
    {
        string[] targets = ["www.youtube.com", "i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg", "i.ytimg.com/vi/other/hq.jpg", "discord.com"];
        Assert.Equal(["www.youtube.com", "i.ytimg.com", "discord.com"], SearchTargets.Hosts(targets));
        Assert.Equal("www.youtube.com, i.ytimg.com, discord.com", SearchTargets.DisplayList(targets));
        Assert.Equal(["youtube", "discord", "rutracker"], SearchTargets.SelectedGroups(SearchTargets.Default));
        Assert.Equal(["rutracker"], SearchTargets.SelectedGroups(["rutracker.org", "www.youtube.com"]));
    }

    [Fact]
    public void GeneratedCandidatesPassWhitelist()
    {
        foreach (var c in AllGenerated())
        {
            Assert.Empty(StrategyValidator.Validate(CandidateGenerator.ToProbeStrategy(c, SearchOptions.TargetsListId)));
            Assert.Empty(StrategyValidator.Validate(CandidateGenerator.ToStrategy(c, "found", "found", ["general", "blocked"], true, "")));
        }
    }

    [Fact]
    public void SavedStrategyGetsQuicAndDiscordVoice()
    {
        string[] lists = ["found", "general", "blocked"];
        foreach (var c in AllGenerated())
        {
            var tcp = CandidateGenerator.ToStrategy(c, "found", "found", lists, true, "");
            var s = UdpProfiles.Add(tcp, quic: true, voice: true, lists);
            Assert.Empty(StrategyValidator.Validate(s));
            Assert.Equal(tcp.Intercept!.Tcp, s.Intercept!.Tcp);
            Assert.Equal("443,19294-19344,50000-50100", s.Intercept.Udp);
            Assert.Equal(tcp.Profiles.Count + 2, s.Profiles.Count);
            var quic = s.Profiles[^2];
            Assert.Equal("filter-udp=443", quic.Args[0]);
            Assert.Equal(lists, quic.Hostlists);
            var voice = s.Profiles[^1];
            Assert.Equal(["filter-udp=19294-19344,50000-50100", "filter-l7=discord,stun"], voice.Args.Take(2));
            Assert.Empty(voice.Hostlists);
        }
    }

    [Fact]
    public void UdpProfilesAreOptionalAndPortsDoNotRepeat()
    {
        var tcp = CandidateGenerator.ToStrategy(AllGenerated().First(), "found", "found", ["general"], true, "");
        Assert.Same(tcp, UdpProfiles.Add(tcp, quic: false, voice: false, ["general"]));
        Assert.Equal("443", UdpProfiles.Add(tcp, quic: true, voice: false, ["general"]).Intercept!.Udp);
        Assert.Equal("19294-19344,50000-50100", UdpProfiles.Add(tcp, quic: false, voice: true, ["general"]).Intercept!.Udp);
        var withQuic = tcp with { Intercept = tcp.Intercept! with { Udp = "443" } };
        Assert.Equal("443,19294-19344,50000-50100", UdpProfiles.Add(withQuic, quic: true, voice: true, ["general"]).Intercept!.Udp);
    }

    [Fact]
    public void RefineChangesExactlyOneThing()
    {
        foreach (var seed in CandidateGenerator.Explore())
            foreach (var v in CandidateGenerator.Refine(seed))
                Assert.StartsWith(seed.Label + "; ", v.Label);
    }

    public static IEnumerable<Candidate> AllGenerated() =>
        CandidateGenerator.Explore().SelectMany(c => CandidateGenerator.Refine(c).Prepend(c)).DistinctBy(c => c.Key);

    private sealed class SyncProgress(Action<SearchProgress> a) : IProgress<SearchProgress>
    {
        public void Report(SearchProgress value) => a(value);
    }
}
