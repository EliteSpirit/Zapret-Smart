using ZapretSmart.Core.Community;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Lists;
using ZapretSmart.Core.Search;
using ZapretSmart.Core.Storage;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Tests;

public sealed class StorageAndCommunityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zs-tests-" + Guid.NewGuid().ToString("N"));

    private static Strategy Sample(string id) => new()
    {
        Id = id,
        Name = "Моя",
        Intercept = new Intercept { Tcp = "443" },
        Profiles = [new Profile { Hostlists = ["general"], Args = ["filter-tcp=443", "dpi-desync=multisplit"] }],
    };

    [Fact]
    public void SavedStrategyLoadsBackIdentical()
    {
        var store = new UserStrategyStore(_dir);
        var s = Sample(UserStrategyStore.NewId());
        store.Save(s);

        var loaded = StrategyLoader.LoadFile(store.PathFor(s.Id));
        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        Assert.Equal(s.Id, loaded.Strategy!.Id);
        Assert.Equal(s.Profiles[0].Args, loaded.Strategy.Profiles[0].Args);
        Assert.Equal(s.Profiles[0].Hostlists, loaded.Strategy.Profiles[0].Hostlists);
    }

    [Fact]
    public void InvalidStrategyIsNotSaved()
    {
        var store = new UserStrategyStore(_dir);
        var bad = Sample("x") with { Profiles = [new Profile { Args = ["dpi-desync=fake", "debug=@C:\\x"] }] };
        Assert.Throws<StrategyRejectedException>(() => store.Save(bad));
        Assert.False(File.Exists(store.PathFor("x")));
    }

    [Fact]
    public void DeleteRejectsPathTraversal()
    {
        Assert.Throws<ArgumentException>(() => new UserStrategyStore(_dir).Delete("..\\..\\settings"));
    }

    [Fact]
    public void SeedCopiesMissingListsWithoutOverwritingUserEdits()
    {
        var bundled = Path.Combine(_dir, "bundled");
        var layout = new EngineLayout(Path.Combine(_dir, "fake"), Path.Combine(_dir, "user"), Path.Combine(_dir, "ipsets"));
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(layout.ListsDir);
        File.WriteAllText(Path.Combine(bundled, "general.txt"), "youtube.com");
        File.WriteAllText(Path.Combine(bundled, "discord.txt"), "discord.com");
        File.WriteAllText(layout.HostlistPath("general"), "my-edit.com");
        File.WriteAllText(Path.Combine(layout.ListsDir, "Bad Name.txt"), "x");

        var store = new ListStore(layout, bundled);
        store.SeedMissing();

        Assert.Equal("my-edit.com", File.ReadAllText(layout.HostlistPath("general")));
        Assert.True(File.Exists(layout.HostlistPath(EngineLayout.AutoListId)));
        Assert.True(File.Exists(layout.HostlistPath(EngineLayout.ExcludeListId)));
        // Подписки видны в выборе сразу, даже до первой загрузки; служебные списки — нет.
        Assert.Equal(["blocked", "discord", "general"], store.HostlistIds());
        Assert.Equal(["blocked-ip"], store.IpsetIds());
    }

    [Fact]
    public void AutoListCanBeCountedAndCleared()
    {
        var layout = new EngineLayout(Path.Combine(_dir, "fake"), Path.Combine(_dir, "user"), Path.Combine(_dir, "ipsets"));
        var store = new ListStore(layout, Path.Combine(_dir, "none"));
        store.SeedMissing();
        File.WriteAllText(layout.HostlistPath(EngineLayout.AutoListId), "a.com\nb.com\n\n");
        Assert.Equal(2, store.AutoListCount());
        store.ClearAutoList();
        Assert.Equal(0, store.AutoListCount());
    }

    private static SearchResult ResultWith(params string[] args) => new(
        ["www.youtube.com"], [], [],
        new CandidateScore(new Candidate("x", args), [], 3, TimeSpan.FromMilliseconds(240), null),
        false);

    [Fact]
    public void ReportContainsOnlyStrategyAndMetrics()
    {
        var r = CommunityReports.FromSearch(ResultWith("dpi-desync=multisplit"), CommunityReports.StandardTargets, 12389, "Москва");
        Assert.NotNull(r);
        Assert.Equal(["dpi-desync=multisplit"], r!.Args);
        Assert.Equal(240, r.MedianLatencyMs);
    }

    [Fact]
    public void NoReportForPersonalTargets()
    {
        Assert.Null(CommunityReports.FromSearch(ResultWith("dpi-desync=multisplit"), ["www.youtube.com", "my-secret-site.org"], 12389, "Москва"));
    }

    [Theory]
    [InlineData(0u, "Москва")]
    [InlineData(12389u, "")]
    [InlineData(12389u, "Москва; DROP")]
    public void NoReportWithoutValidAsnAndRegion(uint asn, string region)
    {
        Assert.Null(CommunityReports.FromSearch(ResultWith("dpi-desync=multisplit"), CommunityReports.StandardTargets, asn, region));
    }

    [Fact]
    public void MaliciousReportFromNetworkIsDropped()
    {
        var evil = new CommunityReport(1, "Москва", ["dpi-desync=fake", "dpi-desync-fake-tls=C:\\Users\\me\\wallet.dat"], 3, 3, 100);
        Assert.Null(CommunityReports.ToStrategy(evil, ["general"]));
        var ok = new CommunityReport(1, "Москва", ["dpi-desync=multisplit", "dpi-desync-split-pos=1"], 3, 3, 100);
        Assert.NotNull(CommunityReports.ToStrategy(ok, ["general"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
