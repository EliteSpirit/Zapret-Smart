using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Search;

/// <summary>
/// UDP-часть найденной стратегии. Поиск проверяет только HTTPS по TCP, и найденная стратегия перехватывала одни
/// TCP 80/443: голос Discord (UDP) и QUIC (UDP 443, на нём Chrome грузит видео и Shorts YouTube) шли без обхода.
/// Проверить их поиском нельзя (голосовой сервер Discord отвечает только участнику звонка), поэтому они добавляются
/// с настройками из готовых стратегий.
/// </summary>
public static class UdpProfiles
{
    public const string QuicPorts = "443";

    /// <summary>Порты голоса Discord: оба диапазона, которые он использует.</summary>
    public const string VoicePorts = "19294-19344,50000-50100";

    /// <summary>QUIC сайтов из списков: фейковый QUIC Initial перед настоящим, как в «Общей» стратегии.</summary>
    public static Profile Quic(IReadOnlyList<string> hostlists) => new()
    {
        Hostlists = hostlists,
        Args = ["filter-udp=" + QuicPorts, "dpi-desync=fake", "dpi-desync-repeats=6", "dpi-desync-fake-quic=quic_initial_www_google_com.bin"],
    };

    /// <summary>Голос Discord: распознаётся по протоколу (discord, stun), а не по списку сайтов.</summary>
    public static Profile Voice() => new()
    {
        Args = ["filter-udp=" + VoicePorts, "filter-l7=discord,stun", "dpi-desync=fake", "dpi-desync-repeats=6"],
    };

    /// <summary>Конец описания стратегии, сохранённой поиском до 0.6.3: по нему видно, что UDP в ней нет.</summary>
    public const string NoUdpMarker = "QUIC не перехватывается.";

    /// <summary>Конец описания после добавления UDP.</summary>
    public const string UdpNote = "UDP: QUIC (видео и Shorts YouTube) и голос Discord, настройки из готовых стратегий, поиском не проверялись.";

    /// <summary>
    /// Стратегия, сохранённая поиском до 0.6.3, с добавленными QUIC и голосом Discord; null, если это не она.
    /// Узнаётся по своему id, концу описания, который ставил только поиск, и отсутствию UDP. Стратегию, которую
    /// человек правил и где описание другое, не трогаем. Списки для QUIC берутся из TCP-профиля.
    /// </summary>
    public static Strategy? UpgradeFoundStrategy(Strategy s)
    {
        if (!s.Id.StartsWith("user-", StringComparison.Ordinal) || s.Intercept?.Udp is { Length: > 0 }) return null;
        if (s.Description?.EndsWith(NoUdpMarker, StringComparison.Ordinal) != true) return null;
        if (s.Profiles.Count != 1 || s.Profiles[0].Args.Any(a => a.StartsWith("filter-udp", StringComparison.Ordinal))) return null;
        var description = s.Description[..^NoUdpMarker.Length].TrimEnd() + " " + UdpNote;
        return Add(s with { Description = description }, quic: true, voice: true, s.Profiles[0].Hostlists);
    }

    /// <summary>Стратегия с добавленными UDP-профилями и перехватом их портов.</summary>
    public static Strategy Add(Strategy s, bool quic, bool voice, IReadOnlyList<string> hostlists)
    {
        if (!quic && !voice) return s;
        var ports = new List<string>();
        if (s.Intercept?.Udp is { Length: > 0 } existing) ports.Add(existing);
        var profiles = s.Profiles.ToList();
        if (quic)
        {
            profiles.Add(Quic(hostlists));
            ports.Add(QuicPorts);
        }
        if (voice)
        {
            profiles.Add(Voice());
            ports.Add(VoicePorts);
        }
        var udp = string.Join(',', ports.SelectMany(p => p.Split(',')).Distinct());
        return s with { Intercept = (s.Intercept ?? new Intercept()) with { Udp = udp }, Profiles = profiles };
    }
}
