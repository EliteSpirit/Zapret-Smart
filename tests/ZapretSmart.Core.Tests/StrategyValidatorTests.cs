using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Tests;

public class StrategyValidatorTests
{
    private static Strategy WithArgs(params string[] args) => new()
    {
        Id = "t",
        Name = "t",
        Intercept = new Intercept { Tcp = "443" },
        Profiles = [new Profile { Args = args }],
    };

    public static readonly string[] AcceptedArgs =
    [
        "dpi-desync=fake,multisplit",
        "filter-l3=ipv4",
        "filter-tcp=80,443,1000-2000",
        "filter-udp=443",
        "filter-l7=tls,quic",
        "dpi-desync-fooling=md5sig,badseq",
        "dpi-desync-repeats=6",
        "dpi-desync-ttl=5",
        "dpi-desync-ttl6=5",
        "dpi-desync-autottl",
        "dpi-desync-autottl=-1:3-20",
        "dpi-desync-autottl6=2",
        "dpi-desync-tcp-flags-set=SYN,ACK",
        "dpi-desync-tcp-flags-unset=0x10",
        "dpi-desync-skip-nosni",
        "dpi-desync-any-protocol=1",
        "dpi-desync-split-pos=1,midsld,host+2,-3",
        "dpi-desync-split-seqovl=681",
        "dpi-desync-split-seqovl=0",
        "dpi-desync-hostfakesplit-midhost=midsld",
        "dpi-desync-hostfakesplit-mod=host=www.google.com,altorder=1",
        "dpi-desync-fakedsplit-mod=altorder=1",
        "dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com",
        "dpi-desync-fake-tcp-mod=seq",
        "dpi-desync-ipfrag-pos-tcp=32",
        "dpi-desync-ipfrag-pos-udp=8",
        "dpi-desync-ts-increment=-600000",
        "dpi-desync-badseq-increment=0x10",
        "dpi-desync-badack-increment=-66000",
        "dpi-desync-udplen-increment=2",
        "dpi-desync-cutoff=n3",
        "dpi-desync-start=d2",
        "dpi-desync-fake-http=0x474554",
        "dpi-desync-fake-tls=!",
        "dpi-desync-fake-tls=tls_clienthello_www_google_com.bin",
        "dpi-desync-fake-tls=0x1603010200",
        "dpi-desync-fake-unknown=zero_256.bin",
        "dpi-desync-fake-syndata=zero_256.bin",
        "dpi-desync-fake-quic=quic_initial_www_google_com.bin",
        "dpi-desync-fake-wireguard=wireguard_initiation.bin",
        "dpi-desync-fake-dht=dht_find_node.bin",
        "dpi-desync-fake-discord=discord-ip-discovery-with-port.bin",
        "dpi-desync-fake-stun=stun.bin",
        "dpi-desync-fake-unknown-udp=zero_256.bin",
        "dpi-desync-udplen-pattern=0x00",
        "dpi-desync-split-seqovl-pattern=tls_clienthello_www_google_com.bin",
        "dpi-desync-fakedsplit-pattern=0x00",
        "dup=2",
        "dup-ttl=5",
        "dup-ttl6=5",
        "dup-autottl",
        "dup-autottl6=-1:3-20",
        "dup-fooling=badsum",
        "dup-cutoff=n2",
        "dup-start=n1",
        "dup-replace",
        "orig-ttl=64",
        "orig-ttl6=64",
        "orig-autottl=+5:3-64",
        "orig-autottl6",
        "orig-mod-start=n1",
        "orig-mod-cutoff=n2",
        "ip-id=zero",
        "wssize=1:6",
        "wssize-cutoff=n2",
        "hostcase",
        "hostspell=hoSt",
        "hostnospace",
        "domcase",
        "methodeol",
    ];

    public static IEnumerable<object[]> AcceptedArgsData() => AcceptedArgs.Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(AcceptedArgsData))]
    public void AcceptsWhitelistedArgs(string arg)
    {
        Assert.Empty(StrategyValidator.Validate(WithArgs("dpi-desync=fake", arg)));
    }

    [Fact]
    public void EveryWhitelistedOptionHasAcceptedSample()
    {
        var covered = AcceptedArgs.Select(a => a.Split('=')[0]).ToHashSet();
        var uncovered = EngineOptionCatalog.Names.Where(n => !covered.Contains(n)).ToList();
        Assert.True(uncovered.Count == 0, "нет примера для: " + string.Join(", ", uncovered));
    }

    [Theory]
    [InlineData("debug=@C:\\log.txt")]
    [InlineData("wf-raw=true")]
    [InlineData("wf-save=C:\\x")]
    [InlineData("pidfile=C:\\x")]
    [InlineData("hostlist=C:\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("ipset=C:\\x")]
    [InlineData("new")]
    [InlineData("--dpi-desync=fake")]
    [InlineData("dpi-desync=evil")]
    [InlineData("dpi-desync=fake,fake,fake,fake")]
    [InlineData("dpi-desync-fooling=md5sig,nope")]
    [InlineData("dpi-desync-repeats=0")]
    [InlineData("dpi-desync-repeats")]
    [InlineData("dpi-desync-split-pos=0")]
    [InlineData("dpi-desync-split-pos=nomarker")]
    [InlineData("filter-tcp=70000")]
    [InlineData("filter-tcp=443 --debug")]
    [InlineData("dpi-desync-fake-tls-mod=sni=bad domain")]
    [InlineData("hostcase=1")]
    public void RejectsForbiddenOrMalformedArgs(string arg)
    {
        Assert.NotEmpty(StrategyValidator.Validate(WithArgs("dpi-desync=fake", arg)));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\Documents\\passwords.txt")]
    [InlineData("..\\..\\secret.bin")]
    [InlineData("../secret.bin")]
    [InlineData("/etc/shadow")]
    [InlineData("@secret.bin")]
    [InlineData("+10@secret.bin")]
    [InlineData("..bin")]
    [InlineData("0x1")]
    [InlineData("!")]
    public void FakeBlobCannotPointOutsideBundledFiles(string value)
    {
        Assert.NotEmpty(StrategyValidator.Validate(WithArgs("dpi-desync=fake", "dpi-desync-fake-quic=" + value)));
    }

    [Fact]
    public void HostlistMustBeIdNotPath()
    {
        var s = WithArgs("dpi-desync=fake") with
        {
            Profiles = [new Profile { Hostlist = "..\\..\\hosts", Args = ["dpi-desync=fake"] }],
        };
        Assert.NotEmpty(StrategyValidator.Validate(s));
    }

    [Fact]
    public void ProfileWithoutActionIsRejected()
    {
        Assert.NotEmpty(StrategyValidator.Validate(WithArgs("filter-tcp=443")));
    }

    [Fact]
    public void InterceptIsRequired()
    {
        var s = WithArgs("dpi-desync=fake") with { Intercept = new Intercept() };
        Assert.NotEmpty(StrategyValidator.Validate(s));
    }

    [Fact]
    public void UnknownJsonFieldsAreRejected()
    {
        const string json = """
            { "id": "x", "name": "x", "intercept": { "tcp": "443" },
              "profiles": [ { "args": ["dpi-desync=fake"], "rawCommandLine": "--debug" } ] }
            """;
        Assert.False(StrategyLoader.Parse(json, "test").IsValid);
    }

    [Fact]
    public void BundledPresetsAreValid()
    {
        var loaded = StrategyLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "presets"));
        Assert.NotEmpty(loaded);
        Assert.All(loaded, l => Assert.True(l.IsValid, l.Source + ": " + string.Join("; ", l.Errors)));
    }
}
