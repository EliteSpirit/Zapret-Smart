using System.Globalization;
using System.Text.RegularExpressions;

namespace ZapretSmart.Core.Strategies;

public enum OptionKind
{
    Plain,
    /// <summary>Значение — байты фейкового пакета: hex или имя файла из комплектной папки fake.</summary>
    Blob,
}

public sealed record EngineOption(string Name, OptionKind Kind, Func<string?, bool> IsValid);

/// <summary>
/// Белый список опций движка, разрешённых внутри стратегии.
/// Всё, чего здесь нет, запрещено: пути к файлам, --debug, --wf-raw, --pidfile, --user, hostlist/ipset и т.п.
/// Стратегия может прийти из сети, а движок работает с правами администратора и читает любые файлы,
/// переданные в --dpi-desync-fake-*, поэтому допускаются только значения, прошедшие проверку.
/// </summary>
public static class EngineOptionCatalog
{
    public static readonly IReadOnlySet<string> DesyncModes = new HashSet<string>
    {
        "fake", "fakeknown", "rst", "rstack", "synack", "syndata",
        "fakeddisorder", "disorder", "fakedsplit", "split", "multisplit", "split2",
        "multidisorder", "disorder2", "hostfakesplit", "ipfrag2", "hopbyhop", "destopt",
        "ipfrag1", "udplen", "tamper",
    };

    public static readonly IReadOnlySet<string> FoolingModes = new HashSet<string>
    {
        "md5sig", "ts", "badsum", "badseq", "datanoack", "hopbyhop", "hopbyhop2",
    };

    public static readonly IReadOnlySet<string> L7Protocols = new HashSet<string>
    {
        "http", "tls", "quic", "wireguard", "dht", "discord", "stun", "unknown",
    };

    private static readonly HashSet<string> PosMarkers = new()
    {
        "host", "endhost", "sld", "midsld", "endsld", "method", "sniext",
    };

    private static readonly HashSet<string> TcpFlagNames = new()
    {
        "FIN", "SYN", "RST", "PSH", "ACK", "URG", "ECE", "CWR", "AE", "R1", "R2", "R3",
    };

    private const int MaxHexBytes = 1460;

    private static readonly Regex DomainRe = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex BundledFileRe = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\.bin$", RegexOptions.CultureInvariant);
    private static readonly Regex HexRe = new(@"^0x([0-9a-fA-F]{2})+$", RegexOptions.CultureInvariant);
    private static readonly Regex AutoTtlRe = new(@"^[+-]?(?<d>\d{1,3})(:(?<min>\d{1,3})(-(?<max>\d{1,3}))?)?$", RegexOptions.CultureInvariant);
    private static readonly Regex CutoffRe = new(@"^[nds]?\d{1,9}$", RegexOptions.CultureInvariant);
    private static readonly Regex WsSizeRe = new(@"^\d{1,5}(:\d{1,2})?$", RegexOptions.CultureInvariant);
    private static readonly Regex PosItemRe = new(@"^(?<abs>[+-]?\d{1,5})$|^(?<m>[a-z]+)(?<off>[+-]\d{1,5})?$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, EngineOption> Options = Build();

    public static bool TryGet(string name, out EngineOption option) => Options.TryGetValue(name, out option!);

    public static IEnumerable<string> Names => Options.Keys;

    private static Dictionary<string, EngineOption> Build()
    {
        var list = new List<EngineOption>
        {
            Plain("filter-l3", EnumList(["ipv4", "ipv6"])),
            Plain("filter-tcp", IsPortList),
            Plain("filter-udp", IsPortList),
            Plain("filter-l7", EnumList(L7Protocols)),

            Plain("dpi-desync", EnumList(DesyncModes, maxItems: 3)),
            Plain("dpi-desync-fooling", EnumList(FoolingModes)),
            Plain("dpi-desync-repeats", Int(1, 1024)),
            Plain("dpi-desync-ttl", Int(0, 255)),
            Plain("dpi-desync-ttl6", Int(0, 255)),
            Plain("dpi-desync-autottl", Optional(IsAutoTtl)),
            Plain("dpi-desync-autottl6", Optional(IsAutoTtl)),
            Plain("dpi-desync-tcp-flags-set", IsTcpFlags),
            Plain("dpi-desync-tcp-flags-unset", IsTcpFlags),
            Plain("dpi-desync-skip-nosni", OptionalBool),
            Plain("dpi-desync-any-protocol", OptionalBool),
            Plain("dpi-desync-split-pos", IsPosList),
            Plain("dpi-desync-split-seqovl", v => v == "0" || IsPos(v)),
            Plain("dpi-desync-hostfakesplit-midhost", IsPos),
            Plain("dpi-desync-hostfakesplit-mod", ModList(v => v is "none" || IsKeyValue(v, "host", IsDomain) || IsKeyValue(v, "altorder", s => s is "0" or "1"))),
            Plain("dpi-desync-fakedsplit-mod", ModList(v => v is "none" || IsKeyValue(v, "altorder", Int(0, 31)))),
            Plain("dpi-desync-fake-tls-mod", ModList(v => v is "none" or "rnd" or "rndsni" or "padencap" or "dupsid" || IsKeyValue(v, "sni", IsDomain))),
            Plain("dpi-desync-fake-tcp-mod", ModList(v => v is "none" or "seq")),
            Plain("dpi-desync-ipfrag-pos-tcp", v => Int(8, 1480)(v) && int.Parse(v!, CultureInfo.InvariantCulture) % 8 == 0),
            Plain("dpi-desync-ipfrag-pos-udp", v => Int(8, 1480)(v) && int.Parse(v!, CultureInfo.InvariantCulture) % 8 == 0),
            Plain("dpi-desync-ts-increment", IsSigned32),
            Plain("dpi-desync-badseq-increment", IsSigned32),
            Plain("dpi-desync-badack-increment", IsSigned32),
            Plain("dpi-desync-udplen-increment", Int(-1500, 1500)),
            Plain("dpi-desync-cutoff", IsCutoff),
            Plain("dpi-desync-start", IsCutoff),

            Blob("dpi-desync-fake-http", allowDefault: false),
            Blob("dpi-desync-fake-tls", allowDefault: true),
            Blob("dpi-desync-fake-unknown", allowDefault: false),
            Blob("dpi-desync-fake-syndata", allowDefault: false),
            Blob("dpi-desync-fake-quic", allowDefault: false),
            Blob("dpi-desync-fake-wireguard", allowDefault: false),
            Blob("dpi-desync-fake-dht", allowDefault: false),
            Blob("dpi-desync-fake-discord", allowDefault: false),
            Blob("dpi-desync-fake-stun", allowDefault: false),
            Blob("dpi-desync-fake-unknown-udp", allowDefault: false),
            Blob("dpi-desync-udplen-pattern", allowDefault: false),
            Blob("dpi-desync-split-seqovl-pattern", allowDefault: false),
            Blob("dpi-desync-fakedsplit-pattern", allowDefault: false),

            Plain("dup", Int(0, 1024)),
            Plain("dup-ttl", Int(0, 255)),
            Plain("dup-ttl6", Int(0, 255)),
            Plain("dup-autottl", Optional(IsAutoTtl)),
            Plain("dup-autottl6", Optional(IsAutoTtl)),
            Plain("dup-fooling", EnumList(FoolingModes)),
            Plain("dup-cutoff", IsCutoff),
            Plain("dup-start", IsCutoff),
            Plain("dup-replace", OptionalBool),
            Plain("orig-ttl", Int(0, 255)),
            Plain("orig-ttl6", Int(0, 255)),
            Plain("orig-autottl", Optional(IsAutoTtl)),
            Plain("orig-autottl6", Optional(IsAutoTtl)),
            Plain("orig-mod-start", IsCutoff),
            Plain("orig-mod-cutoff", IsCutoff),

            Plain("ip-id", v => v is "zero" or "seq" or "seqgroup" or "rnd"),
            Plain("wssize", v => v is not null && WsSizeRe.IsMatch(v)),
            Plain("wssize-cutoff", IsCutoff),
            Plain("hostcase", IsFlag),
            Plain("hostspell", v => v is { Length: 4 } && v.All(char.IsAsciiLetter)),
            Plain("hostnospace", IsFlag),
            Plain("domcase", IsFlag),
            Plain("methodeol", IsFlag),
        };
        return list.ToDictionary(o => o.Name, StringComparer.Ordinal);
    }

    private static EngineOption Plain(string name, Func<string?, bool> rule) => new(name, OptionKind.Plain, rule);

    private static EngineOption Blob(string name, bool allowDefault) =>
        new(name, OptionKind.Blob, v => v is not null && (IsHexBlob(v) || IsBundledFileName(v) || (allowDefault && v == "!")));

    public static bool IsBundledFileName(string v) => BundledFileRe.IsMatch(v) && !v.Contains("..", StringComparison.Ordinal);

    public static bool IsHexBlob(string v) => v.Length <= 2 + MaxHexBytes * 2 && HexRe.IsMatch(v);

    private static bool IsFlag(string? v) => v is null;

    private static bool OptionalBool(string? v) => v is null or "0" or "1";

    private static Func<string?, bool> Optional(Func<string, bool> rule) => v => v is null || rule(v);

    private static Func<string?, bool> Int(int min, int max) => v =>
        v is { Length: > 0 and <= 6 }
        && int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
        && n >= min && n <= max;

    private static bool IsSigned32(string? v)
    {
        if (v is null) return false;
        var s = v.StartsWith('-') ? v[1..] : v;
        if (s.StartsWith("0x", StringComparison.Ordinal))
            return s.Length is > 2 and <= 10 && uint.TryParse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
        return int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    }

    private static Func<string?, bool> EnumList(IReadOnlySet<string> allowed, int maxItems = 16) => v =>
    {
        if (string.IsNullOrEmpty(v)) return false;
        var items = v.Split(',');
        return items.Length <= maxItems && items.All(allowed.Contains);
    };

    private static Func<string?, bool> EnumList(string[] allowed) => EnumList(allowed.ToHashSet());

    private static Func<string?, bool> ModList(Func<string, bool> item) => v =>
        !string.IsNullOrEmpty(v) && v.Split(',') is { Length: <= 8 } items && items.All(item);

    private static bool IsKeyValue(string item, string key, Func<string?, bool> value) =>
        item.StartsWith(key + "=", StringComparison.Ordinal) && value(item[(key.Length + 1)..]);

    public static bool IsDomain(string? v) => v is not null && DomainRe.IsMatch(v);

    public static bool IsPortList(string? v)
    {
        if (string.IsNullOrEmpty(v)) return false;
        foreach (var item in v.Split(','))
        {
            var range = item.Split('-');
            if (range.Length > 2) return false;
            var ports = new int[range.Length];
            for (var i = 0; i < range.Length; i++)
            {
                if (range[i].Length is 0 or > 5 || !range[i].All(char.IsAsciiDigit)) return false;
                ports[i] = int.Parse(range[i], CultureInfo.InvariantCulture);
                if (ports[i] > 65535) return false;
            }
            if (range.Length == 2 && ports[0] > ports[1]) return false;
        }
        return true;
    }

    private static bool IsPos(string? v)
    {
        if (v is null) return false;
        var m = PosItemRe.Match(v);
        if (!m.Success) return false;
        if (m.Groups["abs"].Success)
            return short.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n != 0;
        return PosMarkers.Contains(m.Groups["m"].Value);
    }

    private static bool IsPosList(string? v) =>
        !string.IsNullOrEmpty(v) && v.Split(',') is { Length: <= 16 } items && items.All(IsPos);

    private static bool IsAutoTtl(string v)
    {
        if (v == "-") return true;
        var m = AutoTtlRe.Match(v);
        return m.Success && new[] { "d", "min", "max" }
            .Select(n => m.Groups[n]).Where(g => g.Success)
            .All(g => int.Parse(g.Value, CultureInfo.InvariantCulture) <= 255);
    }

    private static bool IsCutoff(string? v) => v is not null && CutoffRe.IsMatch(v);

    private static bool IsTcpFlags(string? v)
    {
        if (string.IsNullOrEmpty(v)) return false;
        if (v.StartsWith("0x", StringComparison.Ordinal))
            return v.Length is > 2 and <= 5 && v[2..].All(char.IsAsciiHexDigit);
        if (v.All(char.IsAsciiDigit))
            return v.Length <= 4;
        return v.Split(',').All(TcpFlagNames.Contains);
    }
}
