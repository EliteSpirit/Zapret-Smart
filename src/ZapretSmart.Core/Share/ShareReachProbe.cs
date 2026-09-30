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
/// <param name="LoopbackConnects">Порт прокси отвечает на 127.0.0.1: прокси принимает подключения вообще.</param>
/// <param name="Interceptors">Чужие программы с WinDivert (другой zapret, GoodbyeDPI): они перехватывают пакеты раньше нас.</param>
/// <param name="Syns">Кто и сколько раз пытался подключиться к порту прокси (TCP SYN на входе в Windows).</param>
/// <param name="Connected">Кто из чужих адресов дошёл до прокси.</param>
public sealed record ReachReport(
    int Port,
    string? Address,
    TimeSpan Duration,
    bool SelfConnects,
    bool LoopbackConnects,
    IReadOnlyList<string> Interceptors,
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

    /// <summary>Имена программ, которые ставят свой WinDivert и перехватывают пакеты: разные сборки zapret и GoodbyeDPI.</summary>
    private static readonly string[] InterceptorNames = ["winws", "goodbyedpi", "zapret", "nfqws", "ciadpi", "byedpi"];

    /// <summary>Чужие перехватчики среди процессов: по имени, но не из нашей папки движка.</summary>
    public static IReadOnlyList<string> ForeignInterceptors(IEnumerable<(string Name, string? Path, int Pid)> processes, string ownEngineDir)
    {
        var own = System.IO.Path.GetFullPath(ownEngineDir).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return processes
            .Where(p => InterceptorNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .Where(p => p.Path is null || !p.Path.StartsWith(own, StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.Name}.exe (PID {p.Pid}{(p.Path is null ? "" : ", " + ShareLog.Redact(p.Path))})")
            .ToList();
    }

    /// <summary>Чужие перехватчики среди запущенных процессов.</summary>
    public static IReadOnlyList<string> ForeignInterceptors(string ownEngineDir)
    {
        var found = new List<(string, string?, int)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!InterceptorNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
                found.Add((process.ProcessName, path, process.Id));
            }
        }
        return ForeignInterceptors(found, ownEngineDir);
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

        var interceptors = r.Interceptors.Count > 0 ? string.Join(", ", r.Interceptors) : null;
        if (!r.SelfConnects)
        {
            if (interceptors is not null)
            {
                lines.Add($"Порт {r.Port} не отвечает даже с самого ПК, а на ПК работает другой перехватчик пакетов: {interceptors}. Его WinDivert забирает подключения к прокси раньше, чем они до него доходят.");
                lines.Add("Закройте эту программу (или остановите её службу) и повторите проверку. Обход ПК уже делает Zapret Smart, второй не нужен.");
            }
            else if (r.LoopbackConnects)
            {
                lines.Add($"Прокси работает: на 127.0.0.1 порт {r.Port} отвечает. Но подключения на адрес ПК в сети ({r.Address}) пропадают даже с самого ПК: их режет сетевой фильтр на ПК.");
                lines.Add("Так делают антивирусы, VPN-клиенты и программы контроля трафика. Закройте их по очереди полностью (не пауза, а выход из программы) и повторяйте проверку.");
                lines.Add($"Проверьте и сам адрес: в PowerShell выполните Get-NetIPAddress -IPAddress {r.Address} и посмотрите AddressState. Нужно Preferred; Duplicate значит, что этот адрес занят ещё одним устройством в сети.");
            }
            else
            {
                lines.Add($"Порт {r.Port} не отвечает ни на адресе в сети, ни на 127.0.0.1: прокси не принимает подключения.");
                lines.Add("Выключите и включите раздачу и пришлите журнал раздачи.");
            }
            if (third is not null)
                lines.Add($"Установлен {third}. Пауза защиты не всегда выгружает его сетевой драйвер: чтобы исключить его, выйдите из него полностью (значок в трее, «Выход»).");
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
            if (interceptors is not null) lines.Add($"Ещё на ПК работает другой перехватчик пакетов: {interceptors}. Закройте его и повторите проверку.");
            if (!r.Firewall.BlocksAllInbound && third is null && interceptors is null)
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
