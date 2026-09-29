using System.Diagnostics;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Search;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Tests;

/// <summary>
/// Прогоняет argv через настоящий парсер движка (--dry-run), собранного из engine/nfq:
/// winws.exe на Windows или nfqws на Linux. Ловит расхождение белого списка с тем, что движок реально принимает.
/// Запускается, если ZS_ENGINE указывает на собранный движок. WinDivert при --dry-run не открывается.
/// </summary>
public class EngineDryRunTests
{
    private static readonly string? Engine = Environment.GetEnvironmentVariable("ZS_ENGINE");

    private static readonly EngineLayout Layout = CreateLayout();

    private static EngineLayout CreateLayout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zs-dryrun-" + Environment.ProcessId);
        var layout = new EngineLayout(Path.Combine(AppContext.BaseDirectory, "fake"), Path.Combine(dir, "lists"), Path.Combine(dir, "ipsets"));
        Directory.CreateDirectory(layout.ListsDir);
        Directory.CreateDirectory(layout.IpsetsDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "lists", "general.txt"), layout.HostlistPath("general"), overwrite: true);
        File.WriteAllLines(layout.HostlistPath("blocked"), ["rutracker.org", "xn--80ak6aa92e.xn--p1ai"]);
        File.WriteAllText(layout.HostlistPath(EngineLayout.ExcludeListId), "");
        File.WriteAllText(layout.HostlistPath(EngineLayout.AutoListId), "");
        File.WriteAllLines(layout.IpsetPath("blocked-ip"), ["203.0.113.0/24", "2001:db8:1::/48"]);
        return layout;
    }

    public sealed class EngineTheoryAttribute : TheoryAttribute
    {
        public EngineTheoryAttribute()
        {
            if (string.IsNullOrEmpty(Engine)) Skip = "ZS_ENGINE не задан";
        }
    }

    public static IEnumerable<object[]> Presets() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "presets"), "*.json")
            .Select(f => new object[] { Path.GetFileName(f) });

    public static IEnumerable<object[]> AcceptedArgs() =>
        StrategyValidatorTests.AcceptedArgs.Select(a => new object[] { a });

    public static IEnumerable<object[]> SearchCandidates() =>
        StrategySearchTests.AllGenerated().Select(c => new object[] { c.Label, string.Join('\n', c.Args) });

    [EngineTheory]
    [MemberData(nameof(Presets))]
    public void PresetIsAcceptedByEngine(string file)
    {
        var loaded = StrategyLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "presets", file));
        AssertEngineAccepts(EngineCommandBuilder.Build(loaded.Strategy!, Layout).Argv);
    }

    [EngineTheory]
    [MemberData(nameof(AcceptedArgs))]
    public void WhitelistedArgIsAcceptedByEngine(string arg)
    {
        var s = new Strategy
        {
            Id = "t",
            Name = "t",
            Intercept = new Intercept { Tcp = "443" },
            Profiles = [new Profile { Args = ["dpi-desync=fake", arg] }],
        };
        AssertEngineAccepts(EngineCommandBuilder.Build(s, Layout).Argv);
    }

    [EngineTheory]
    [MemberData(nameof(SearchCandidates))]
    public void SearchCandidateIsAcceptedByEngine(string label, string args)
    {
        _ = label;
        var c = new Candidate(label, args.Split('\n'));
        AssertEngineAccepts(EngineCommandBuilder.Build(CandidateGenerator.ToStrategy(c, "t", "t", ["general", "blocked"], true, ""), Layout).Argv);
    }

    [EngineTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListOptionsAreAcceptedByEngine(bool withMissingLists)
    {
        var s = EngineCommandBuilderTests.One(new Profile
        {
            Hostlists = withMissingLists ? ["general", "blocked", "not-downloaded"] : ["general", "blocked"],
            Ipsets = withMissingLists ? ["blocked-ip", "not-downloaded"] : ["blocked-ip"],
            AutoHostlist = true,
            Args = ["filter-tcp=443", "dpi-desync=multisplit"],
        });
        var cmd = EngineCommandBuilder.Build(s, Layout);
        Assert.Equal(withMissingLists ? 2 : 0, cmd.Warnings.Count);
        AssertEngineAccepts(cmd.Argv);
    }

    /// <summary>
    /// Фильтр WinDivert, который строит движок: раздача берёт только исходящие с портов прокси и ответы на них,
    /// обход ПК их исключает. Проверяется текст фильтра из --wf-save, до WinDivert дело не доходит.
    /// </summary>
    [EngineTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void EngineFilterSplitsTrafficByProxyPorts(bool share)
    {
        if (!Path.GetFileNameWithoutExtension(Engine!).Equals("winws", StringComparison.OrdinalIgnoreCase)) return;
        var s = StrategyLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "presets", Directory.EnumerateFiles(
            Path.Combine(AppContext.BaseDirectory, "presets"), "*.json").Select(Path.GetFileName).Order().First()!)).Strategy!;
        var file = Path.Combine(Path.GetTempPath(), "zs-wf-" + Guid.NewGuid().ToString("N") + ".txt");
        var argv = EngineCommandBuilder.Build(s, Layout, share ? EngineScope.Share : EngineScope.Pc).Argv.Append("--wf-save=" + file).ToList();
        var (code, output) = RunEngine(argv);
        Assert.True(code == 0 && File.Exists(file), output);
        var filter = File.ReadAllText(file).ReplaceLineEndings("\n");
        var (low, high) = EngineCommandBuilder.ShareLocalPorts;
        var part = $"(tcp and (outbound and tcp.SrcPort >= {low} and tcp.SrcPort <= {high} or inbound and tcp.DstPort >= {low} and tcp.DstPort <= {high}))";
        Assert.Contains("\nand\n" + (share ? "" : "!") + part, filter);
    }

    [EngineTheory]
    [InlineData("--wf-lport=5-1")]
    [InlineData("--wf-lport=0-10")]
    [InlineData("--wf-lport=1-70000")]
    [InlineData("--wf-lport=10")]
    [InlineData("--wf-lport=10-20x")]
    public void EngineRejectsBadProxyPortRange(string arg)
    {
        if (!Path.GetFileNameWithoutExtension(Engine!).Equals("winws", StringComparison.OrdinalIgnoreCase)) return;
        var (code, output) = RunEngine(["--dry-run", "--wf-tcp=443", arg]);
        Assert.True(code != 0, output);
    }

    private static (int Code, string Output) RunEngine(IEnumerable<string> argv)
    {
        var psi = new ProcessStartInfo(Engine!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr);
    }

    private static void AssertEngineAccepts(IReadOnlyList<string> argv)
    {
        var psi = new ProcessStartInfo(Engine!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--dry-run");
        var isWinws = Path.GetFileNameWithoutExtension(Engine!).Equals("winws", StringComparison.OrdinalIgnoreCase);
        // --wf-* есть только в winws; nfqws вместо них нужен --qnum.
        if (!isWinws) psi.ArgumentList.Add("--qnum=200");
        foreach (var a in argv.Where(a => isWinws || !a.StartsWith("--wf-", StringComparison.Ordinal)))
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"движок отклонил: {string.Join(' ', psi.ArgumentList)}\n{stdout.Result}\n{stderr}");
    }
}
