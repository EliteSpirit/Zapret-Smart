using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml;

namespace ZapretSmart.Core.Share;

/// <summary>Фильтр WFP из выгрузки «netsh wfp show filters»: то, что нужно, чтобы понять, чей он и что делает.</summary>
public sealed record WfpFilter(ulong Id, string Name, string Provider, string Layer, string Action, bool Persistent)
{
    /// <summary>
    /// Может отбросить подключение: блок или callout, который решает сам (терминирующий или с неизвестным исходом).
    /// Инспектирующий callout только смотрит на пакеты и отбросить их не может.
    /// </summary>
    public bool CanDrop => Action is "FWP_ACTION_BLOCK" or "FWP_ACTION_CALLOUT_TERMINATING" or "FWP_ACTION_CALLOUT_UNKNOWN";

    /// <summary>Терминирующий callout решает всегда сам: из всех фильтров он первый подозреваемый.</summary>
    public bool AlwaysDecides => Action is "FWP_ACTION_BLOCK" or "FWP_ACTION_CALLOUT_TERMINATING";

    /// <summary>Стоит на пути входящего подключения к программе на ПК.</summary>
    public bool OnInbound => Layer.Contains("RECV_ACCEPT", StringComparison.Ordinal)
        || Layer.Contains("INBOUND_IPPACKET", StringComparison.Ordinal)
        || Layer.Contains("INBOUND_TRANSPORT", StringComparison.Ordinal)
        || Layer.Contains("AUTH_LISTEN", StringComparison.Ordinal);
}

/// <summary>Найденная программа, которая может не пускать телефон к раздаче.</summary>
/// <param name="DroppedTest">Именно её фильтр отбросил тестовое подключение (по журналу аудита).</param>
/// <param name="GuardsInbound">У неё есть фильтры, которые могут отбросить входящее подключение.</param>
/// <param name="AlwaysDecides">Среди них есть блок или терминирующий callout: они решают сами, без оглядки на других.</param>
public sealed record ConflictFinding(string Product, string Evidence, string Advice, bool DroppedTest, bool GuardsInbound, bool AlwaysDecides = false);

/// <summary>Итог поиска.</summary>
/// <param name="LanConnects">Подключение к тестовому порту по адресу ПК в сети прошло.</param>
/// <param name="LoopbackConnects">Подключение к тому же порту по 127.0.0.1 прошло.</param>
/// <param name="Details">Подробности для журнала: что выгрузилось, какие события и фильтры нашлись.</param>
public sealed record ConflictReport(
    IPAddress Address,
    bool LanConnects,
    bool LoopbackConnects,
    IReadOnlyList<ConflictFinding> Findings,
    IReadOnlyList<string> Details);

/// <summary>
/// «Найти мешающие программы»: то, что на живом ПК пользователя нашло Portmaster после недели догадок. Тестовое
/// подключение к своему порту по адресу в сети и по 127.0.0.1, журнал аудита WFP (какой фильтр отбросил подключение),
/// выгрузка фильтров WFP и процессы, сверенные со списком известных программ. Для каждой найденной программы сказано,
/// что в ней выключить. Ничего не меняет, кроме аудита WFP на время проверки: его прежнее состояние восстанавливается.
/// </summary>
public static class ConflictScan
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Подкатегории аудита по GUID, чтобы не зависеть от языка Windows: отброшенные пакеты и подключения.</summary>
    private static readonly string[] AuditSubcategories =
        ["{0CCE9225-69AE-11D9-BED3-505054503030}", "{0CCE9226-69AE-11D9-BED3-505054503030}"];

    private sealed record Product(string Name, string[] Processes, string FilterPattern, string Advice);

    private const string VpnAdvice = "Выключите в {0} kill switch (защиту от утечек) или включите доступ к локальной сети (LAN). Если он не нужен, закройте его или удалите и перезагрузите ПК: фильтры kill switch иногда остаются после закрытия.";

    /// <summary>
    /// Программы, которые встраиваются в сетевой стек и умеют не пускать входящие подключения. Узнаются по процессам
    /// и по именам их фильтров WFP (провайдер в выгрузке часто без имени, а имя фильтра почти всегда с названием).
    /// </summary>
    private static readonly Product[] Products =
    [
        new("Portmaster", ["portmaster-core", "portmaster", "portmaster-start"], @"\bPortmaster\b",
            "Portmaster блокирует все входящие по умолчанию. Откройте Portmaster → Apps → ZapretSmart и выключите «Force Block Incoming Connections» и «Force Block LAN». Глобальную настройку не трогайте."),
        new("Kaspersky", ["avp", "avpui"], @"\bKaspersky\b",
            "Kaspersky → Настройки → Сетевой экран → Сетевые правила программ → ZapretSmart: разрешите входящие подключения или переведите программу в «Доверенные». Пауза защиты не выгружает его сетевые драйверы."),
        new("ESET", ["ekrn", "egui"], @"\bESET\b|\bEpfw",
            "ESET → Настройка → Защита сети → Файервол → Правила: разрешите входящие для ZapretSmart.exe (или включите автоматический режим)."),
        new("Avast или AVG", ["AvastSvc", "AVGSvc", "aswEngSrv"], @"\bAvast\b|\bAVG\b",
            "Брандмауэр Avast/AVG → Приложения: разрешите ZapretSmart входящие; домашнюю сеть отметьте как частную (доверенную)."),
        new("Bitdefender", ["bdservicehost", "vsserv", "bdagent"], @"\bBitdefender\b",
            "Bitdefender → Защита → Брандмауэр → Правила: разрешите ZapretSmart.exe; сетевой адаптер отметьте как «Дом/Офис»."),
        new("Norton", ["NortonSecurity", "nsWscSvc"], @"\bNorton\b|\bSymantec\b",
            "Norton → Параметры → Брандмауэр → Управление программами: разрешите ZapretSmart."),
        new("Comodo", ["cmdagent"], @"\bComodo\b",
            "Comodo Firewall → Правила приложений: разрешите входящие для ZapretSmart.exe."),
        new("GlassWire", ["GlassWire", "GWCtlSrv"], @"\bGlassWire\b",
            "GlassWire → Брандмауэр: разрешите ZapretSmart (или переключите режим «Спрашивать о новых»)."),
        new("simplewall", ["simplewall"], @"\bsimplewall\b",
            "simplewall: отметьте ZapretSmart.exe в списке разрешённых и примените фильтры."),
        new("Windows Firewall Control", ["wfc", "wfcs"], @"Windows Firewall Control",
            "Windows Firewall Control мог удалить разрешающее правило Zapret Smart «защитой правил»: разрешите ZapretSmart.exe в нём и включите раздачу заново."),
        new("TinyWall", ["TinyWall"], @"\bTinyWall\b",
            "TinyWall → Управление → Исключения: добавьте ZapretSmart.exe."),
        new("NetLimiter", ["NLSvc", "NLClientApp"], @"\bNetLimiter\b",
            "NetLimiter: снимите для ZapretSmart правило, запрещающее входящие."),
        new("AdGuard", ["AdguardSvc", "Adguard"], @"\bAdGuard\b",
            "AdGuard → Настройки → Сеть: исключите ZapretSmart.exe из фильтрации."),
        new("ZoogVPN", ["zoogvpn"], @"\bZoog", string.Format(VpnAdvice, "ZoogVPN")),
        new("NordVPN", ["nordvpn", "nordvpn-service", "NordVPN"], @"\bNordVPN\b|\bNordLynx\b", string.Format(VpnAdvice, "NordVPN")),
        new("Proton VPN", ["ProtonVPN", "ProtonVPNService", "ProtonVPN.WireGuardService"], @"\bProton\b", string.Format(VpnAdvice, "Proton VPN")),
        new("Surfshark", ["Surfshark", "Surfshark.Service"], @"\bSurfshark\b", string.Format(VpnAdvice, "Surfshark")),
        new("ExpressVPN", ["expressvpn", "ExpressVPN.AppService", "expressvpnd"], @"\bExpressVPN\b", string.Format(VpnAdvice, "ExpressVPN")),
        new("Private Internet Access", ["pia-service", "pia-client"], @"Private Internet Access", string.Format(VpnAdvice, "Private Internet Access")),
        new("Mullvad", ["mullvad-daemon", "Mullvad VPN"], @"\bMullvad\b", string.Format(VpnAdvice, "Mullvad")),
        new("Windscribe", ["WindscribeService", "Windscribe"], @"\bWindscribe\b", string.Format(VpnAdvice, "Windscribe")),
    ];

    /// <summary>Фильтры из выгрузки «netsh wfp show filters». Битые элементы пропускаются.</summary>
    public static IReadOnlyList<WfpFilter> ParseFilters(XmlDocument doc)
    {
        var list = new List<WfpFilter>();
        var items = doc.SelectNodes("//filters/item");
        if (items is null) return list;
        foreach (XmlNode item in items)
        {
            if (!ulong.TryParse(item.SelectSingleNode("filterId")?.InnerText, out var id)) continue;
            var flags = item.SelectNodes("flags/item");
            var persistent = false;
            if (flags is not null)
                foreach (XmlNode flag in flags)
                    persistent |= flag.InnerText == "FWPM_FILTER_FLAG_PERSISTENT";
            list.Add(new WfpFilter(
                id,
                item.SelectSingleNode("displayData/name")?.InnerText ?? "",
                item.SelectSingleNode("providerKey")?.InnerText ?? "",
                item.SelectSingleNode("layerKey")?.InnerText ?? "",
                item.SelectSingleNode("action/type")?.InnerText ?? "",
                persistent));
        }
        return list;
    }

    /// <summary>
    /// ID фильтров, отбросивших подключение к порту, из вывода «wevtutil qe Security /f:xml» (события 5152 и 5157).
    /// wevtutil выдаёт события подряд без общего корня, поэтому они оборачиваются перед разбором.
    /// </summary>
    public static IReadOnlyList<ulong> ParseDropEvents(string wevtutilXml, int port)
    {
        var ids = new List<ulong>();
        var body = Regex.Replace(wevtutilXml, @"<\?xml[^>]*\?>", "");
        var doc = new XmlDocument();
        try
        {
            doc.LoadXml("<events>" + body + "</events>");
        }
        catch (XmlException)
        {
            return ids;
        }
        foreach (XmlNode ev in doc.DocumentElement!.ChildNodes)
        {
            var data = new Dictionary<string, string>();
            foreach (XmlNode node in ev.ChildNodes)
            {
                if (node.LocalName != "EventData") continue;
                foreach (XmlNode d in node.ChildNodes)
                    if (d.Attributes?["Name"]?.Value is { } name) data[name] = d.InnerText;
            }
            var ours = data.GetValueOrDefault("DestPort") == port.ToString() || data.GetValueOrDefault("SourcePort") == port.ToString();
            if (ours && ulong.TryParse(data.GetValueOrDefault("FilterRTID"), out var id) && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Фильтры Windows и Microsoft: их разбирает проверка брандмауэра, здесь они не «мешающие программы».</summary>
    private static bool IsWindowsOwn(WfpFilter f) =>
        f.Provider.StartsWith("FWPM_PROVIDER_", StringComparison.Ordinal)
        || Regex.IsMatch(f.Name, @"\bWindows\b|\bMicrosoft\b|\bTeredo\b|\bUWP\b|Фильтр времени загрузки|Boot Time Filter");

    /// <summary>
    /// Сверка найденного со списком программ. droppedIds: фильтры, отбросившие тест по журналу. interceptors: чужие
    /// перехватчики пакетов (другой zapret, GoodbyeDPI). Сначала то, что точно отбросило тест, потом то, что стоит
    /// на входящих, потом остальное.
    /// </summary>
    public static IReadOnlyList<ConflictFinding> Match(
        IReadOnlyCollection<string> processNames,
        IReadOnlyList<WfpFilter> filters,
        IReadOnlyCollection<ulong> droppedIds,
        IReadOnlyList<string> interceptors)
    {
        var findings = new List<ConflictFinding>();
        foreach (var product in Products)
        {
            var procs = processNames.Where(p => product.Processes.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var own = filters.Where(f => Regex.IsMatch(f.Name, product.FilterPattern, RegexOptions.IgnoreCase)).ToList();
            if (procs.Count == 0 && own.Count == 0) continue;
            var dropping = own.Where(f => f.CanDrop).ToList();
            var inbound = dropping.Count(f => f.OnInbound);
            var decides = dropping.Any(f => f.OnInbound && f.AlwaysDecides);
            var dropped = own.Any(f => droppedIds.Contains(f.Id));
            var evidence = new List<string>();
            if (procs.Count > 0) evidence.Add("работает " + string.Join(", ", procs.Select(p => p + ".exe")));
            if (own.Count > 0) evidence.Add($"фильтров WFP: {own.Count}, из них могут отбросить входящее подключение: {inbound}");
            if (dropped) evidence.Add("его фильтр отбросил тестовое подключение");
            findings.Add(new ConflictFinding(product.Name, string.Join("; ", evidence), product.Advice, dropped, inbound > 0, decides));
        }
        foreach (var other in interceptors)
            findings.Add(new ConflictFinding("Другой перехватчик пакетов", other,
                "Закройте его или остановите его службу: он забирает пакеты через свой WinDivert. Обход ПК уже делает Zapret Smart.", false, true, true));

        // Отбросил тест фильтр, которого нет в списке программ: назвать его хотя бы по имени фильтра.
        foreach (var id in droppedIds)
        {
            if (findings.Any(f => f.DroppedTest)) break;
            if (filters.FirstOrDefault(f => f.Id == id) is { } filter && !IsWindowsOwn(filter))
                findings.Add(new ConflictFinding($"Фильтр «{filter.Name}»", $"id {filter.Id}, слой {filter.Layer}, провайдер {filter.Provider}",
                    "Этой программы нет в списке известных. По имени фильтра найдите её среди установленных и разрешите в ней входящие для ZapretSmart.exe.", true, true, true));
        }
        return findings.OrderByDescending(f => f.DroppedTest).ThenByDescending(f => f.AlwaysDecides).ThenByDescending(f => f.GuardsInbound).ToList();
    }

    /// <summary>Вывод для человека: итог первой строкой, потом по строке на найденную программу.</summary>
    public static IReadOnlyList<string> Explain(ConflictReport r)
    {
        var lines = new List<string>();
        if (r.LanConnects)
        {
            lines.Add($"Подключения к {r.Address} проходят: программы на ПК раздаче не мешают.");
            if (r.Findings.Count > 0)
                lines.Add("Установлены, но сейчас не мешают: " + string.Join(", ", r.Findings.Select(f => f.Product)) + ". Если телефон всё равно не подключается, дело в сети: роутер, гостевой Wi-Fi, VPN на телефоне.");
            return lines;
        }
        lines.Add(r.LoopbackConnects
            ? $"Подключение к {r.Address} не проходит даже с самого ПК, а к 127.0.0.1 проходит: его режет программа на ПК."
            : $"Подключение не проходит ни к {r.Address}, ни к 127.0.0.1: на ПК заблокированы даже локальные подключения.");
        if (r.Findings.Count == 0)
        {
            lines.Add("Известных мешающих программ не нашлось. Подробности в журнале раздачи: там все блокирующие фильтры сторонних программ.");
            return lines;
        }
        foreach (var f in r.Findings)
            lines.Add($"{(f.DroppedTest ? "Виновник" : f.AlwaysDecides ? "Скорее всего" : f.GuardsInbound ? "Может мешать" : "Установлен")}: {f.Product} ({f.Evidence}). {f.Advice}");
        return lines;
    }

    /// <summary>Проводит поиск. Нужны права администратора (аудит и выгрузка WFP); без них часть шагов пропускается.</summary>
    public static async Task<ConflictReport> RunAsync(IPAddress address, string ownEngineDir, string workDir, CancellationToken ct)
    {
        var details = new List<string>();
        Directory.CreateDirectory(workDir);
        var auditBackup = Path.Combine(workDir, "audit-backup.csv");
        var auditChanged = false;
        bool lan = false, loopback = false;
        IReadOnlyList<ulong> dropped = [];
        var started = DateTime.UtcNow;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Прежнюю политику аудита сохраняем целиком и восстанавливаем её же, а не просто выключаем.
                var backup = await Tool("auditpol", ["/backup", "/file:audit-backup.csv"], ct, workDir);
                if (backup.ExitCode == 0)
                {
                    foreach (var sub in AuditSubcategories) await Tool("auditpol", ["/set", $"/subcategory:{sub}", "/failure:enable"], ct);
                    auditChanged = true;
                }
                else details.Add("аудит WFP не включился: " + FirstLine(backup.Output + backup.Errors + backup.StartError));
            }

            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                lan = await ShareReachProbe.SelfConnectsAsync(address, port, ct);
                loopback = await ShareReachProbe.SelfConnectsAsync(IPAddress.Loopback, port, ct);
            }
            finally
            {
                listener.Stop();
            }
            details.Add($"тестовый порт {port}: {address} {(lan ? "проходит" : "не проходит")}, 127.0.0.1 {(loopback ? "проходит" : "не проходит")}");

            if (auditChanged)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var ms = (long)(DateTime.UtcNow - started).TotalMilliseconds + 5000;
                var events = await Tool("wevtutil",
                    ["qe", "Security", $"/q:*[System[(EventID=5152 or EventID=5157) and TimeCreated[timediff(@SystemTime) <= {ms}]]]", "/f:xml", "/rd:true", "/c:200"], ct);
                dropped = ParseDropEvents(events.Output, port);
                details.Add(dropped.Count > 0
                    ? "по журналу аудита тест отбросили фильтры: " + string.Join(", ", dropped)
                    : "в журнале аудита отброшенного теста нет: режет драйвер без записи в журнал (так делают Portmaster и антивирусы)");
            }
        }
        finally
        {
            if (auditChanged)
            {
                var restore = await Tool("auditpol", ["/restore", "/file:audit-backup.csv"], CancellationToken.None, workDir);
                if (restore.ExitCode != 0)
                {
                    foreach (var sub in AuditSubcategories) await Tool("auditpol", ["/set", $"/subcategory:{sub}", "/failure:disable"], CancellationToken.None);
                    details.Add("прежнюю политику аудита восстановить не удалось, аудит WFP выключен");
                }
            }
            TryDelete(auditBackup);
        }

        IReadOnlyList<WfpFilter> filters = [];
        if (OperatingSystem.IsWindows())
        {
            var file = Path.Combine(workDir, "wfp-filters.xml");
            var export = await Tool("netsh", ["wfp", "show", "filters", "file=wfp-filters.xml"], ct, workDir);
            try
            {
                var doc = new XmlDocument();
                doc.Load(file);
                filters = ParseFilters(doc);
                details.Add($"фильтров WFP: {filters.Count}");
            }
            catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
            {
                details.Add("выгрузка фильтров WFP не прочиталась: " + e.Message + " " + FirstLine(export.Output + export.StartError));
            }
            TryDelete(file);
            foreach (var f in filters.Where(f => f.CanDrop && f.OnInbound && !IsWindowsOwn(f)).Take(40))
                details.Add($"  фильтр {f.Id}: {f.Name} [{f.Action}, {f.Layer}{(f.Persistent ? ", постоянный" : "")}]");
        }

        var processes = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { processes.Add(p.ProcessName); } catch (InvalidOperationException) { }
            }
        }
        var interceptors = OperatingSystem.IsWindows() ? ShareReachProbe.ForeignInterceptors(ownEngineDir) : [];
        var findings = Match(processes, filters, dropped, interceptors);
        return new ConflictReport(address, lan, loopback, findings, details);
    }

    /// <summary>
    /// Системная программа Windows. Файлы ей передаются относительными именами в рабочей папке: путь с пробелом
    /// (имя пользователя) netsh и auditpol разбирают по-своему.
    /// </summary>
    private static Task<ProcessResult> Tool(string file, string[] args, CancellationToken ct, string? workDir = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir ?? "",
            StandardOutputEncoding = ProcessRunner.ConsoleEncoding,
            StandardErrorEncoding = ProcessRunner.ConsoleEncoding,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return ProcessRunner.RunAsync(psi, ToolTimeout, ct);
    }

    private static string FirstLine(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
