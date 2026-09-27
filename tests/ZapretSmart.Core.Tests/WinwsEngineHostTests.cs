using System.Diagnostics;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Search;

namespace ZapretSmart.Core.Tests;

/// <summary>
/// Управление процессом движка на подставном исполняемом файле (sh-скрипт вместо winws):
/// ожидание строки готовности, таймаут, выход до готовности, остановка и принудительное завершение.
/// </summary>
public sealed class WinwsEngineHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zs-host-" + Guid.NewGuid().ToString("N"));

    public sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "подставной движок — sh-скрипт";
        }
    }

    public WinwsEngineHostTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "lists"));
        File.WriteAllText(Path.Combine(_dir, "lists", SearchOptions.TargetsListId + ".txt"), "example.com");
    }

    private string PidFile => Path.Combine(_dir, "pid");

    private WinwsEngineHost HostWith(string script, TimeSpan? timeout = null)
    {
        var exe = Path.Combine(_dir, "fake-winws");
        File.WriteAllText(exe, "#!/bin/sh\n[ \"$1\" = --probe ] && exit 0\necho $$ > '" + PidFile + "'\n" + script + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WaitUntilExecutable(exe);
        return new WinwsEngineHost(exe, new EngineLayout(Path.Combine(AppContext.BaseDirectory, "fake"), Path.Combine(_dir, "lists"), Path.Combine(_dir, "ipsets")),
            timeout ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Только что записанный скрипт Linux может отказаться исполнять с ETXTBSY («Text file busy»): если другой тест
    /// в этом же процессе сделал fork, пока файл был открыт на запись, дочерний процесс держит дескриптор до своего exec.
    /// Один удачный пробный запуск значит, что таких держателей больше нет, а новым взяться неоткуда: файл уже закрыт.
    /// </summary>
    private static void WaitUntilExecutable(string exe)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--probe") { UseShellExecute = false })!;
                p.WaitForExit();
                return;
            }
            catch (System.ComponentModel.Win32Exception) when (attempt < 100)
            {
                Thread.Sleep(10);
            }
        }
    }

    private static readonly Candidate Split = new("x", ["dpi-desync=multisplit"]);
    private static ZapretSmart.Core.Strategies.Strategy Probe => CandidateGenerator.ToProbeStrategy(Split, SearchOptions.TargetsListId);

    private int EnginePid() => int.Parse(File.ReadAllText(PidFile).Trim());

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AssertGone(int pid)
    {
        var sw = Stopwatch.StartNew();
        while (IsAlive(pid) && sw.ElapsedMilliseconds < 5000) Thread.Sleep(50);
        Assert.False(IsAlive(pid), "процесс движка не завершён");
    }

    [UnixFact]
    public async Task StartReturnsOnReadyMarkerAndSessionStopsEngine()
    {
        using var host = HostWith("echo 'windivert initialized. capture is started.'\nexec sleep 30");
        var session = await host.StartAsync(Probe, CancellationToken.None);
        var pid = EnginePid();
        Assert.True(IsAlive(pid));
        await session.DisposeAsync();
        AssertGone(pid);
    }

    [UnixFact]
    public async Task ExitBeforeReadyIsStartFailureWithOutput()
    {
        using var host = HostWith("echo 'A copy of winws is already running with the same filter'\nexit 1");
        var e = await Assert.ThrowsAsync<EngineStartException>(() => host.StartAsync(Probe, CancellationToken.None));
        Assert.Contains("already running", e.Message);
        Assert.Contains("кодом 1", e.Message);
    }

    [UnixFact]
    public async Task StderrReasonSurvivesStdoutFlushedAtExit()
    {
        // winws так и делает: причина в stderr сразу, а буфер stdout выливается при выходе и идёт после неё.
        using var host = HostWith("echo 'A copy of winws is already running with the same filter' >&2\nsleep 0.2\nfor i in 1 2 3 4 5 6 7 8; do echo \"profile line $i\"; done\necho\nexit 1");
        var e = await Assert.ThrowsAsync<EngineStartException>(() => host.StartAsync(Probe, CancellationToken.None));
        Assert.Contains("already running", e.Message);
        Assert.Contains("profile line 8", e.Message);
    }

    [UnixFact]
    public async Task MissingMarkerTimesOutAndKillsEngine()
    {
        using var host = HostWith("echo 'starting'\nexec sleep 30", TimeSpan.FromMilliseconds(700));
        var e = await Assert.ThrowsAsync<EngineStartException>(() => host.StartAsync(Probe, CancellationToken.None));
        Assert.Contains("не начал перехват", e.Message);
        AssertGone(EnginePid());
    }

    [UnixFact]
    public async Task CancellationWhileWaitingKillsEngine()
    {
        using var host = HostWith("exec sleep 30");
        using var cts = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(Probe, cts.Token));
        AssertGone(EnginePid());
    }

    [UnixFact]
    public async Task HostDisposeKillsLiveEngineSynchronously()
    {
        var host = HostWith("echo 'capture is started'\nexec sleep 30");
        await host.StartAsync(Probe, CancellationToken.None);
        var pid = EnginePid();
        Assert.True(IsAlive(pid));
        host.Dispose();
        AssertGone(pid);
    }

    [UnixFact]
    public async Task EngineReceivesBuiltArguments()
    {
        var args = Path.Combine(_dir, "args");
        using var host = HostWith($"printf '%s\\n' \"$@\" > '{args}'\necho 'capture is started'\nexec sleep 30");
        await using (await host.StartAsync(Probe, CancellationToken.None)) { }
        var lines = File.ReadAllLines(args);
        Assert.Equal(["--wf-tcp=443", "--hostlist=" + Path.Combine(_dir, "lists", SearchOptions.TargetsListId + ".txt"), "--hostlist-domains=" + EngineCommandBuilder.GuardDomain, "--filter-tcp=443", "--dpi-desync=multisplit"], lines);
    }

    [UnixFact]
    public async Task EngineThatCannotBeExecutedIsStartFailure()
    {
        // Так выглядит блокировка антивирусом или битый файл: Process.Start бросает Win32Exception.
        var exe = Path.Combine(_dir, "not-executable");
        File.WriteAllText(exe, "garbage");
        using var host = new WinwsEngineHost(exe,
            new EngineLayout(Path.Combine(AppContext.BaseDirectory, "fake"), Path.Combine(_dir, "lists"), Path.Combine(_dir, "ipsets")), TimeSpan.FromSeconds(1));
        var e = await Assert.ThrowsAsync<EngineStartException>(() => host.StartAsync(Probe, CancellationToken.None));
        Assert.Contains("не удалось запустить движок", e.Message);
    }

    [Fact]
    public async Task MissingExecutableIsNotSwallowed()
    {
        using var host = new WinwsEngineHost(Path.Combine(_dir, "nope.exe"),
            new EngineLayout(Path.Combine(AppContext.BaseDirectory, "fake"), Path.Combine(_dir, "lists"), Path.Combine(_dir, "ipsets")), TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<FileNotFoundException>(() => host.StartAsync(Probe, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
