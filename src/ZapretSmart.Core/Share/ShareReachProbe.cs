using System.Diagnostics;
using System.Net;
using ZapretSmart.Core.Updates;

namespace ZapretSmart.Core.Share;

/// <summary>Брандмауэры на ПК, которые могут не пустить телефон к прокси.</summary>
/// <param name="BlocksAllInbound">В брандмауэре Windows для текущей сети включено «Блокировать все входящие подключения»:
/// оно отменяет любые разрешающие правила, наше тоже.</param>
/// <param name="ThirdParty">Сторонние брандмауэры, зарегистрированные в Центре безопасности (антивирусы). Правила
/// Windows для них ничего не значат.</param>
public sealed record FirewallState(bool BlocksAllInbound, IReadOnlyList<string> ThirdParty)
{
    public static readonly FirewallState Unknown = new(false, []);
}

/// <summary>Что выяснила проверка связи с телефоном.</summary>
/// <param name="SelfConnects">Порт прокси отвечает при подключении с самого ПК на его адрес в сети.</param>
/// <param name="Syns">Кто и сколько раз пытался подключиться к порту прокси (TCP SYN на входе в Windows).</param>
/// <param name="Connected">Кто из чужих адресов дошёл до прокси.</param>
public sealed record ReachReport(
    int Port,
    string? Address,
    TimeSpan Duration,
    bool SelfConnects,
    FirewallState Firewall,
    IReadOnlyDictionary<IPAddress, int> Syns,
    IReadOnlyCollection<IPAddress> Connected,
    string? SniffError);

/// <summary>
/// Проверка «почему телефон не достучался». Делит путь на три участка: слушает ли прокси (подключение с самого ПК),
/// доходят ли до ПК попытки подключения (перехват SYN через WinDivert на слое NETWORK, до брандмауэра) и доходят ли
/// они до приложения (событие прокси). По тому, где цепочка рвётся, понятно, что чинить: приложение, брандмауэр на ПК
/// или сеть (роутер, телефон).
/// </summary>
public static class ShareReachProbe
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Попытки подключиться к порту прокси снаружи: первый пакет TCP-рукопожатия.</summary>
    public static string SynFilter(int port) =>
        $"inbound and !loopback and ip and tcp.DstPort == {port} and tcp.Syn and !tcp.Ack";

    /// <summary>Адрес отправителя TCP SYN из IPv4-пакета; null для остального.</summary>
    public static IPAddress? SynSource(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 20 || packet[0] >> 4 != 4 || packet[9] != 6) return null;
        return new IPAddress(packet.Slice(12, 4));
    }

    /// <summary>Политика «блокировать все входящие» в выводе «netsh advfirewall show currentprofile» (слово не переводится).</summary>
    public static bool BlocksAllInbound(string netshCurrentProfile) =>
        netshCurrentProfile.Contains("BlockInboundAlways", StringComparison.OrdinalIgnoreCase);

    /// <summary>Состояние брандмауэров. Если узнать не вышло, считается, что ничего особенного нет.</summary>
    public static async Task<FirewallState> ReadFirewallAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return FirewallState.Unknown;
        var netsh = await ProcessRunner.RunAsync(
            new ProcessStartInfo("netsh") { ArgumentList = { "advfirewall", "show", "currentprofile" } }, ToolTimeout, ct);
        var ps = new ProcessStartInfo("powershell.exe")
        {
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            ArgumentList =
            {
                "-NoProfile", "-Command",
                "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
                "Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct -ErrorAction SilentlyContinue | ForEach-Object { $_.displayName }",
            },
        };
        UpdateInstaller.IsolateFromPowerShell7(ps);
        var products = await ProcessRunner.RunAsync(ps, ToolTimeout, ct);
        var thirdParty = products.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FirewallState(BlocksAllInbound(netsh.Output), thirdParty);
    }

    /// <summary>Подключается к порту прокси с самого ПК. Такой трафик брандмауэр не фильтрует: проверяется только, слушает ли прокси.</summary>
    public static async Task<bool> SelfConnectsAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var client = new System.Net.Sockets.TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(address, port, limit.Token);
            return true;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Вывод для человека: где рвётся связь и что делать. Первая строка итог, остальные подробности.</summary>
    public static IReadOnlyList<string> Explain(ReachReport r)
    {
        var lines = new List<string>();
        var third = r.Firewall.ThirdParty.Count > 0 ? string.Join(", ", r.Firewall.ThirdParty) : null;
        var seconds = (int)r.Duration.TotalSeconds;

        if (!r.SelfConnects)
        {
            lines.Add($"Порт {r.Port} не отвечает даже с самого ПК: прокси не слушает, или его соединения перехватывает антивирус.");
            lines.Add("Выключите и включите раздачу и посмотрите журнал раздачи: там будет, на каком шаге запуск остановился.");
            if (third is not null) lines.Add($"Установлен {third}: на время проверки отключите его сетевой экран.");
            return lines;
        }

        if (r.Connected.Count > 0)
        {
            lines.Add($"Связь в порядке: {string.Join(", ", r.Connected)} дошёл до прокси.");
            return lines;
        }

        if (r.SniffError is not null)
        {
            lines.Add($"Прокси отвечает, но за {seconds} с телефон до него не дошёл, а проверить, доходят ли пакеты до ПК, не удалось: {r.SniffError}.");
            AddFirewallHints(lines, r, third);
            return lines;
        }

        if (r.Syns.Count > 0)
        {
            var from = string.Join(", ", r.Syns.Select(p => $"{p.Key} ({p.Value})"));
            lines.Add($"Подключения доходят до ПК, но не до приложения: их отбрасывает брандмауэр на ПК. Пытались подключиться: {from}.");
            AddFirewallHints(lines, r, third);
            if (!r.Firewall.BlocksAllInbound && third is null)
                lines.Add("Разрешающее правило стоит, запретов Windows нет. Остаются групповая политика брандмауэра или сетевой фильтр, который не регистрируется в Windows (некоторые VPN и антивирусы).");
            return lines;
        }

        lines.Add($"За {seconds} с до ПК не дошло ни одной попытки подключиться к порту {r.Port}. Телефон шлёт их не сюда, или их режет сеть.");
        lines.Add($"Проверьте: телефон в той же сети Wi-Fi, что и ПК, и не в гостевой; на роутере выключена изоляция клиентов (AP isolation, «Изоляция точки доступа»); на телефоне выключены VPN и «Частный узел» iCloud; телефон открывает адрес {r.Address ?? "ПК"}:{r.Port}.");
        if (third is not null)
            lines.Add($"Ещё {third} может отбрасывать пакеты раньше, чем их увидит Windows: на минуту отключите его сетевой экран и повторите проверку.");
        return lines;
    }

    private static void AddFirewallHints(List<string> lines, ReachReport r, string? third)
    {
        if (r.Firewall.BlocksAllInbound)
            lines.Add("В брандмауэре Windows для этой сети включено «Блокировать все входящие подключения, в том числе для разрешённых программ». Оно отменяет разрешение приложения: выключите его.");
        if (third is not null)
            lines.Add($"Установлен {third}: правила брандмауэра Windows для него ничего не значат. Разрешите в нём входящие подключения на порт {r.Port} для ZapretSmart.exe.");
    }
}
