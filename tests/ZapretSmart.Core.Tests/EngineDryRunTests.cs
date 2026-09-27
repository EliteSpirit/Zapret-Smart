using System.Diagnostics;
using ZapretSmart.Core.Engine;
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

    private static readonly EngineLayout Layout = new(
        Path.Combine(AppContext.BaseDirectory, "fake"),
        Path.Combine(AppContext.BaseDirectory, "lists"));

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

    [EngineTheory]
    [MemberData(nameof(Presets))]
    public void PresetIsAcceptedByEngine(string file)
    {
        var loaded = StrategyLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "presets", file));
        AssertEngineAccepts(EngineCommandBuilder.Build(loaded.Strategy!, Layout));
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
        AssertEngineAccepts(EngineCommandBuilder.Build(s, Layout));
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
