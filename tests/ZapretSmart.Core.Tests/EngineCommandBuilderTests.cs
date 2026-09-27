using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Tests;

public class EngineCommandBuilderTests
{
    private static readonly EngineLayout Layout = new(
        Path.Combine(AppContext.BaseDirectory, "fake"),
        Path.Combine(AppContext.BaseDirectory, "lists"));

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
                    Hostlist = "general",
                    Args = ["filter-tcp=443", "dpi-desync=fake,multisplit", "dpi-desync-fake-tls=tls_clienthello_www_google_com.bin", "dpi-desync-autottl"],
                },
            ],
        };

        var argv = EngineCommandBuilder.Build(s, Layout);

        Assert.Equal(
        [
            "--wf-tcp=443",
            "--wf-udp=443",
            "--filter-udp=443",
            "--dpi-desync=fake",
            "--new",
            "--hostlist=" + Path.Combine(Layout.ListsDir, "general.txt"),
            "--filter-tcp=443",
            "--dpi-desync=fake,multisplit",
            "--dpi-desync-fake-tls=" + Path.Combine(Layout.FakeDir, "tls_clienthello_www_google_com.bin"),
            "--dpi-desync-autottl",
        ], argv);
    }

    [Fact]
    public void HexAndDefaultBlobsArePassedAsIs()
    {
        var s = new Strategy
        {
            Id = "t",
            Name = "t",
            Intercept = new Intercept { Tcp = "443" },
            Profiles = [new Profile { Args = ["dpi-desync=fake", "dpi-desync-fake-tls=!", "dpi-desync-fake-http=0x474554"] }],
        };

        var argv = EngineCommandBuilder.Build(s, Layout);

        Assert.Contains("--dpi-desync-fake-tls=!", argv);
        Assert.Contains("--dpi-desync-fake-http=0x474554", argv);
    }

    [Fact]
    public void MissingBundledFileOrListIsRejected()
    {
        var s = new Strategy
        {
            Id = "t",
            Name = "t",
            Intercept = new Intercept { Tcp = "443" },
            Profiles = [new Profile { Hostlist = "nope", Args = ["dpi-desync=fake", "dpi-desync-fake-tls=nope.bin"] }],
        };

        var e = Assert.Throws<StrategyRejectedException>(() => EngineCommandBuilder.Build(s, Layout));
        Assert.Equal(2, e.Errors.Count);
    }

    [Fact]
    public void InvalidStrategyIsRejectedEvenIfCallerSkippedValidation()
    {
        var s = new Strategy
        {
            Id = "t",
            Name = "t",
            Intercept = new Intercept { Tcp = "443" },
            Profiles = [new Profile { Args = ["dpi-desync=fake", "debug=@C:\\x.txt"] }],
        };

        Assert.Throws<StrategyRejectedException>(() => EngineCommandBuilder.Build(s, Layout));
    }

    [Fact]
    public void AllPresetsBuildAgainstBundledFiles()
    {
        foreach (var l in StrategyLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "presets")))
            Assert.NotEmpty(EngineCommandBuilder.Build(l.Strategy!, Layout));
    }
}
