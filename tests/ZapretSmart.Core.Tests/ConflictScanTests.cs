using System.Diagnostics;
using System.Net;
using System.Xml;
using ZapretSmart.Core.Share;

namespace ZapretSmart.Core.Tests;

/// <summary>
/// «Найти мешающие программы». Данные в тестах повторяют выгрузки с ПК пользователя, где раздачу не пускал
/// Portmaster, а рядом стоял Kaspersky со своими фильтрами.
/// </summary>
public sealed class ConflictScanTests
{
    private static WfpFilter F(ulong id, string name, string layer, string action, string provider = "{p}") =>
        new(id, name, provider, layer, action, false);

    private static readonly WfpFilter[] UserMachine =
    [
        F(94994, "Portmaster Packet Inbound IPv4", "FWPM_LAYER_INBOUND_IPPACKET_V4", "FWP_ACTION_CALLOUT_TERMINATING", ""),
        F(311098, "Portmaster ALE Outbound IPv4", "FWPM_LAYER_ALE_AUTH_CONNECT_V4", "FWP_ACTION_CALLOUT_TERMINATING", ""),
        F(85574, "Kaspersky Lab WFP ALE auth accept V4 filter", "FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4", "FWP_ACTION_CALLOUT_UNKNOWN"),
        F(85580, "Kaspersky Lab stream inspection", "FWPM_LAYER_STREAM_V4", "FWP_ACTION_CALLOUT_INSPECTION"),
        F(306543, "UWP Default Inbound Block Rule", "FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4", "FWP_ACTION_BLOCK", "FWPM_PROVIDER_MPSSVC_APP_ISOLATION"),
        F(85576, "Teredo socket option opt out block filter", "FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6", "FWP_ACTION_BLOCK"),
    ];

    [Fact]
    public void UsersCasePutsPortmasterFirstAndTellsWhatToSwitchOff()
    {
        var findings = ConflictScan.Match(["portmaster-core", "ZapretSmart", "avp", "explorer"], UserMachine, [], []);
        Assert.Equal(["Portmaster", "Kaspersky"], findings.Select(f => f.Product));
        Assert.True(findings[0].AlwaysDecides);
        Assert.Contains("Force Block Incoming Connections", findings[0].Advice);
        Assert.Contains("portmaster-core.exe", findings[0].Evidence);
        Assert.False(findings[1].AlwaysDecides);
        Assert.True(findings[1].GuardsInbound);

        var lines = ConflictScan.Explain(new ConflictReport(IPAddress.Parse("192.168.31.40"), false, true, findings, []));
        Assert.Contains("не проходит даже с самого ПК, а к 127.0.0.1 проходит", lines[0]);
        Assert.StartsWith("Скорее всего: Portmaster", lines[1]);
        Assert.StartsWith("Может мешать: Kaspersky", lines[2]);
        Assert.All(lines, l => Assert.DoesNotContain("—", l));
    }

    [Fact]
    public void TheFilterThatDroppedTheTestWinsAndUnknownOwnersAreNamedByTheFilter()
    {
        var byLog = ConflictScan.Match(["portmaster-core", "avp"], UserMachine, [85574], []);
        Assert.Equal("Kaspersky", byLog[0].Product);
        Assert.True(byLog[0].DroppedTest);
        Assert.StartsWith("Виновник: Kaspersky",
            ConflictScan.Explain(new ConflictReport(IPAddress.Loopback, false, true, byLog, []))[1]);

        var unknown = ConflictScan.Match([], [.. UserMachine, F(7, "SuperShield inbound guard", "FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4", "FWP_ACTION_BLOCK")], [7], []);
        Assert.Equal("Фильтр «SuperShield inbound guard»", unknown[0].Product);
        Assert.True(unknown[0].DroppedTest);

        // Отбросил фильтр Windows: это не «мешающая программа», её разбирает проверка брандмауэра.
        Assert.DoesNotContain(ConflictScan.Match([], UserMachine, [306543], []), f => f.DroppedTest);
    }

    [Fact]
    public void NothingIsReportedWithoutEvidenceAndGenericNamesDoNotMatch()
    {
        Assert.Empty(ConflictScan.Match(["explorer", "ns", "cis", "chrome"], [F(1, "Nordic keyboard", "X", "FWP_ACTION_BLOCK")], [], []));
        var other = ConflictScan.Match([], [], [], ["winws.exe (PID 42)"]);
        Assert.Equal("Другой перехватчик пакетов", other.Single().Product);

        var fine = ConflictScan.Explain(new ConflictReport(IPAddress.Parse("192.168.31.40"), true, true,
            ConflictScan.Match(["portmaster-core"], [], [], []), []));
        Assert.StartsWith("Подключения к 192.168.31.40 проходят", fine[0]);
        Assert.Contains("Установлены, но сейчас не мешают: Portmaster", fine[1]);
    }

    [Fact]
    public void FiltersAreReadFromTheNetshExport()
    {
        var doc = new XmlDocument();
        doc.LoadXml("""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfpdiag><filters numItems="3">
              <item><filterKey>{a}</filterKey><displayData><name>Portmaster Packet Inbound IPv4</name><description>x</description></displayData>
                <flags numItems="1"><item>FWPM_FILTER_FLAG_PERSISTENT</item></flags><providerKey>{p1}</providerKey>
                <layerKey>FWPM_LAYER_INBOUND_IPPACKET_V4</layerKey><action><type>FWP_ACTION_CALLOUT_TERMINATING</type></action><filterId>94994</filterId></item>
              <item><filterKey>{b}</filterKey><displayData><name>no id</name></displayData><action><type>FWP_ACTION_BLOCK</type></action></item>
              <item><filterKey>{c}</filterKey><displayData><name>permit</name></displayData><flags/><layerKey>FWPM_LAYER_ALE_AUTH_CONNECT_V4</layerKey>
                <action><type>FWP_ACTION_PERMIT</type></action><filterId>5</filterId></item>
            </filters></wfpdiag>
            """);
        var filters = ConflictScan.ParseFilters(doc);
        Assert.Equal(2, filters.Count);
        var pm = filters[0];
        Assert.Equal((94994UL, "Portmaster Packet Inbound IPv4", "{p1}", true), (pm.Id, pm.Name, pm.Provider, pm.Persistent));
        Assert.True(pm.CanDrop && pm.OnInbound && pm.AlwaysDecides);
        Assert.False(filters[1].CanDrop);
    }

    [Fact]
    public void DropEventsForThePortAreReadFromWevtutilOutput()
    {
        const string ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        string Ev(int id, string dst, string filter) => $"""
            <Event xmlns="{ns}"><System><EventID>{id}</EventID></System><EventData>
            <Data Name="Application">\device\harddiskvolume3\x\zapretsmart.exe</Data><Data Name="SourcePort">51000</Data>
            <Data Name="DestAddress">192.168.31.40</Data><Data Name="DestPort">{dst}</Data><Data Name="FilterRTID">{filter}</Data></EventData></Event>
            """;
        var output = Ev(5157, "18080", "273563") + Ev(5152, "443", "1") + Ev(5152, "18080", "273563") + Ev(5152, "18080", "90588");
        Assert.Equal([273563UL, 90588UL], ConflictScan.ParseDropEvents(output, 18080));
        Assert.Empty(ConflictScan.ParseDropEvents("not xml <", 18080));
        Assert.Empty(ConflictScan.ParseDropEvents("", 18080));
    }

    public sealed class AdminWindowsFactAttribute : FactAttribute
    {
        public AdminWindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ZS_LIVE") != "1")
                Skip = "нужны Windows и права администратора (ZS_LIVE=1): поиск включает аудит WFP и выгружает фильтры";
        }
    }

    private static string AuditState()
    {
        var psi = new ProcessStartInfo("auditpol") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "/get", "/subcategory:{0CCE9225-69AE-11D9-BED3-505054503030},{0CCE9226-69AE-11D9-BED3-505054503030}", "/r" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    /// <summary>
    /// На раннере никто не мешает: подключение по адресу в сети проходит, фильтры выгружаются, поиск укладывается
    /// в пару минут, а политика аудита после него ровно такая, как до.
    /// </summary>
    [AdminWindowsFact]
    public async Task ScanRunsOnWindowsAndLeavesTheAuditPolicyAsItWas()
    {
        var address = NetworkPolicy.FindLocalAddresses().FirstOrDefault()?.Address ?? IPAddress.Loopback;
        var before = AuditState();
        var dir = Path.Combine(Path.GetTempPath(), "zs-conflicts-" + Guid.NewGuid().ToString("N"));
        var sw = Stopwatch.StartNew();
        var report = await ConflictScan.RunAsync(address, Path.Combine(dir, "engine"), dir, CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromMinutes(3), sw.Elapsed.ToString());
        Assert.True(report.LanConnects, string.Join("\n", report.Details));
        Assert.True(report.LoopbackConnects);
        Assert.Contains(report.Details, d => d.StartsWith("фильтров WFP: ", StringComparison.Ordinal));
        Assert.Equal(before, AuditState());
        Assert.False(File.Exists(Path.Combine(dir, "audit-backup.csv")));
        Assert.False(File.Exists(Path.Combine(dir, "wfp-filters.xml")));
        Directory.Delete(dir, recursive: true);
    }
}
