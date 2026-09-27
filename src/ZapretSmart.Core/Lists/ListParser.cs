using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Lists;

public enum ListKind { Domains, Ips }

public sealed record ParsedList(IReadOnlyList<string> Entries, int Invalid, int TooBroad, int Reserved);

/// <summary>
/// Разбор скачанных списков. Содержимое из сети не доверенное: каждая строка проверяется,
/// в файл для движка попадают только нормализованные домены или подсети.
/// </summary>
public static class ListParser
{
    /// <summary>
    /// Подсети крупнее /16 (больше 65 536 адресов) отбрасываются. В реальных списках там Cloudflare (104.16.0.0/12)
    /// и Google (142.250.0.0/15): обход на них ломал бы тысячи незаблокированных сайтов.
    /// Для IPv6 порог /48 (выделение одной площадке): /32 — это целиком сеть провайдера, например 2a00:1450::/32 у Google.
    /// </summary>
    public const int MinPrefixV4 = 16;
    public const int MinPrefixV6 = 48;

    private static readonly (IPAddress Net, int Prefix)[] ReservedNets =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
            "224.0.0.0/4", "240.0.0.0/4", "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8", "2001:db8::/32",
        }.Select(ParseCidrUnchecked),
    ];

    public static ParsedList Parse(string text, ListKind kind) => kind == ListKind.Domains ? ParseDomains(text) : ParseIps(text);

    public static ParsedList ParseDomains(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var invalid = 0;
        foreach (var raw in Lines(text))
        {
            var d = raw.ToLowerInvariant().TrimEnd('.');
            if (EngineOptionCatalog.IsDomain(d)) set.Add(d);
            else invalid++;
        }
        return new ParsedList(set.Order(StringComparer.Ordinal).ToList(), invalid, 0, 0);
    }

    public static ParsedList ParseIps(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        int invalid = 0, broad = 0, reserved = 0;
        foreach (var raw in Lines(text))
        {
            if (!TryParseCidr(raw, out var net, out var prefix))
            {
                invalid++;
                continue;
            }
            var v4 = net.AddressFamily == AddressFamily.InterNetwork;
            if (prefix < (v4 ? MinPrefixV4 : MinPrefixV6))
            {
                broad++;
                continue;
            }
            if (ReservedNets.Any(r => Overlaps(r.Net, r.Prefix, net, prefix)))
            {
                reserved++;
                continue;
            }
            set.Add($"{net}/{prefix}");
        }
        return new ParsedList(set.Order(StringComparer.Ordinal).ToList(), invalid, broad, reserved);
    }

    private static IEnumerable<string> Lines(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim();
            var hash = l.IndexOf('#');
            if (hash >= 0) l = l[..hash].Trim();
            if (l.Length > 0) yield return l;
        }
    }

    public static bool TryParseCidr(string s, out IPAddress net, out int prefix)
    {
        net = IPAddress.None;
        prefix = 0;
        var slash = s.IndexOf('/');
        var addrPart = slash < 0 ? s : s[..slash];
        if (addrPart.Length == 0 || addrPart.Any(c => !(char.IsAsciiHexDigit(c) || c is '.' or ':')))
            return false;
        if (!IPAddress.TryParse(addrPart, out var addr)) return false;
        if (addr.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
        // IPAddress.TryParse читает "010.0.0.1" как восьмеричное 8.0.0.1; в списках такое — опечатка, а не намерение.
        if (addr.AddressFamily == AddressFamily.InterNetwork
            && (addrPart.Count(c => c == '.') != 3 || addrPart.Split('.').Any(o => o.Length == 0 || o.Length > 3 || (o.Length > 1 && o[0] == '0') || !o.All(char.IsAsciiDigit))))
            return false;

        var max = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        prefix = max;
        if (slash >= 0 && !(int.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) && prefix <= max))
            return false;
        net = Mask(addr, prefix);
        return true;
    }

    private static (IPAddress, int) ParseCidrUnchecked(string s)
    {
        TryParseCidr(s, out var net, out var prefix);
        return (net, prefix);
    }

    private static IPAddress Mask(IPAddress addr, int prefix)
    {
        var bytes = addr.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        return new IPAddress(bytes);
    }

    private static bool Overlaps(IPAddress a, int aPrefix, IPAddress b, int bPrefix) =>
        a.AddressFamily == b.AddressFamily && Mask(a, Math.Min(aPrefix, bPrefix)).Equals(Mask(b, Math.Min(aPrefix, bPrefix)));
}
