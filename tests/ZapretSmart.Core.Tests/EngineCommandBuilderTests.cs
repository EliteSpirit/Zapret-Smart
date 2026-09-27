using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Tests;

public sealed class EngineCommandBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zs-builder-" + Guid.NewGuid().ToString("N"));
    private readonly EngineLayout _layout;

    public EngineCommandBuilderTests()
    {
        _layout = new EngineLayout(Path.Combine(AppContext.BaseDirectory, "fake"), Path.Combine(_dir, "lists"), Path.Combine(_dir, "ipsets"));
        Directory.CreateDirectory(_layout.ListsDir);
        Directory.CreateDirectory(_layout.IpsetsDir);
        File.WriteAllText(_layout.HostlistPath("general"), "youtube.com\n");
        File.WriteAllText(_layout.IpsetPath("blocked-ip"), "203.0.113.0/24\n");
    }

    public static Strategy One(Profile p, string? udp = null) => new()
    {
        Id = "t",
        Name = "t",
        Intercept = new Intercept { Tcp = "443", Udp = udp },
        Profiles = [p],
    };

    [Fact]
    public void BuildsArgvInOrderWithProfileSeparators()
    {
        var s = new Strategy
        {
            Id = "t",
            Name = "t",
            Intercept = new Intercept { Tcp = "443", Udp = "443" },
            Profiles =
            [
                new Profile { Args = ["filter-udp=443", "dpi-desync=fake"] },
                new Profile
                {
                    Hostlists = ["general"],
                    Args = ["filter-tcp=443", "dpi-desync=fake,multisplit", "dpi-desync-fake-tls=tls_clienthello_www_google_com.bin", "dpi-desync-autottl"],
                },
            ],
        };

        var cmd = EngineCommandBuilder.Build(s, _layout);

        Assert.Empty(cmd.Warnings);
        Assert.Equal(
        [
            "--wf-tcp=443",
            "--wf-udp=443",
            "--filter-udp=443",
            "--dpi-desync=fake",
            "--new",
            "--hostlist=" + _layout.HostlistPath("general"),
            "--hostlist-domains=" + EngineCommandBuilder.GuardDomain,
            "--filter-tcp=443",
            "--dpi-desync=fake,multisplit",
            "--dpi-desync-fake-tls=" + Path.Combine(_layout.FakeDir, "tls_clienthello_www_google_com.bin"),
            "--dpi-desync-autottl",
        ], cmd.Argv);
    }

    [Fact]
    public void ProfileWithoutListsGetsNoGuard()
    {
        var argv = EngineCommandBuilder.Build(One(new Profile { Args = ["dpi-desync=fake"] }), _layout).Argv;
        Assert.DoesNotContain(argv, a => a.StartsWith("--hostlist", StringComparison.Ordinal) || a.StartsWith("--ipset", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingListIsWarningAndGuardStillNarrowsProfile()
    {
        // Список ещё не скачан: без страховки профиль остался бы без include-листов и бил бы по всему трафику.
        var cmd = EngineCommandBuilder.Build(One(new Profile { Hostlists = ["blocked"], Ipsets = ["nope"], Args = ["dpi-desync=fake"] }), _layout);

        Assert.Equal(2, cmd.Warnings.Count);
        Assert.Contains("--hostlist-domains=" + EngineCommandBuilder.GuardDomain, cmd.Argv);
        Assert.Contains("--ipset-ip=" + EngineCommandBuilder.GuardIp, cmd.Argv);
        Assert.DoesNotContain(cmd.Argv, a => a.StartsWith("--hostlist=", StringComparison.Ordinal) || a.StartsWith("--ipset=", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoHostlistCreatesFileAddsGuardAndExclude()
    {
        File.WriteAllText(_layout.HostlistPath(EngineLayout.ExcludeListId), "bank.ru\n");
        var argv = EngineCommandBuilder.Build(One(new Profile { AutoHostlist = true, Args = ["dpi-desync=fake"] }), _layout).Argv;

        Assert.True(File.Exists(_layout.HostlistPath(EngineLayout.AutoListId)));
        Assert.Contains("--hostlist-auto=" + _layout.HostlistPath(EngineLayout.AutoListId), argv);
        Assert.Contains("--hostlist-domains=" + EngineCommandBuilder.GuardDomain, argv);
        Assert.Contains("--hostlist-exclude=" + _layout.HostlistPath(EngineLayout.ExcludeListId), argv);
    }

    [Fact]
    public void IpsetsResolveToIpsetsDir()
    {
        var argv = EngineCommandBuilder.Build(One(new Profile { Ipsets = ["blocked-ip"], Args = ["filter-udp=443", "dpi-desync=fake"] }, "443"), _layout).Argv;
        Assert.Contains("--ipset=" + _layout.IpsetPath("blocked-ip"), argv);
        Assert.Contains("--ipset-ip=" + EngineCommandBuilder.GuardIp, argv);
    }

    [Fact]
    public void HexAndDefaultBlobsArePassedAsIs()
    {
        var argv = EngineCommandBuilder.Build(One(new Profile { Args = ["dpi-desync=fake", "dpi-desync-fake-tls=!", "dpi-desync-fake-http=0x474554"] }), _layout).Argv;
        Assert.Contains("--dpi-desync-fake-tls=!", argv);
        Assert.Contains("--dpi-desync-fake-http=0x474554", argv);
    }

    [Fact]
    public void MissingBundledFileIsRejected()
    {
        var e = Assert.Throws<StrategyRejectedException>(() =>
            EngineCommandBuilder.Build(One(new Profile { Args = ["dpi-desync=fake", "dpi-desync-fake-tls=nope.bin"] }), _layout));
        Assert.Single(e.Errors);
    }

    [Fact]
    public void InvalidStrategyIsRejectedEvenIfCallerSkippedValidation()
    {
        Assert.Throws<StrategyRejectedException>(() =>
            EngineCommandBuilder.Build(One(new Profile { Args = ["dpi-desync=fake", "debug=@C:\\x.txt"] }), _layout));
    }

    [Fact]
    public void AllPresetsBuild()
    {
        foreach (var l in StrategyLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "presets")))
            Assert.NotEmpty(EngineCommandBuilder.Build(l.Strategy!, _layout).Argv);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
