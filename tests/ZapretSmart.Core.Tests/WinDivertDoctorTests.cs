using System.Diagnostics;
using ZapretSmart.Core.Engine;

namespace ZapretSmart.Core.Tests;

public sealed class WinDivertDoctorTests
{
    [Theory]
    [InlineData(@"\??\C:\Zapret Smart\engine\WinDivert64.sys", @"C:\Zapret Smart\engine\WinDivert64.sys")]
    [InlineData(@"""C:\Old\WinDivert64.sys""", @"C:\Old\WinDivert64.sys")]
    [InlineData(@"\SystemRoot\System32\drivers\WinDivert64.sys", @"C:\Windows\System32\drivers\WinDivert64.sys")]
    [InlineData(@"System32\drivers\WinDivert64.sys", @"C:\Windows\System32\drivers\WinDivert64.sys")]
    [InlineData(@"\\server\share\WinDivert64.sys", @"\\server\share\WinDivert64.sys")]
    public void ServiceImagePathBecomesFilePath(string imagePath, string expected)
    {
        Assert.Equal(expected, WinDivertDoctor.DriverPath(imagePath, @"C:\Windows"));
    }

    [Fact]
    public void NoImagePathMeansNoService()
    {
        Assert.Null(WinDivertDoctor.DriverPath(null, @"C:\Windows"));
        Assert.Null(WinDivertDoctor.DriverPath("  ", @"C:\Windows"));
    }

    [Fact]
    public void KnownWinDivertErrorsAreExplained()
    {
        Assert.Contains("нет WinDivert64.sys", WinDivertDoctor.Explain(2, driverNextToEngine: false));
        Assert.Contains("на месте", WinDivertDoctor.Explain(2, driverNextToEngine: true));
        foreach (var code in new[] { 5, 577, 654, 1072, 1275, 1753 })
            Assert.False(string.IsNullOrEmpty(WinDivertDoctor.Explain(code, true)), $"код {code}");
        Assert.Null(WinDivertDoctor.Explain(1, true));
        Assert.Null(WinDivertDoctor.Explain(87, true));
    }

    [Fact]
    public void RepairDoesNothingOutsideWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(WinDivertDoctor.RepairStaleService());
    }

    /// <summary>
    /// Служба драйвера с пропавшим файлом удаляется, а с файлом на месте остаётся. Имя службы своё, чтобы не тронуть
    /// настоящую службу WinDivert, которую в этом же прогоне используют живые тесты движка.
    /// </summary>
    [WindowsAutostartTests.AdminWindowsFact]
    public async Task StaleDriverServiceIsRemovedAndLiveOneKept()
    {
        var name = "ZsTestDivert" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), "zs-divert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var driver = Path.Combine(dir, "test.sys");
        File.WriteAllText(driver, "not a driver");
        try
        {
            Assert.Equal(0, await Sc("create", name, "type=", "kernel", "start=", "demand", "binPath=", driver));
            Assert.Null(WinDivertDoctor.RepairStaleService(serviceName: name));
            Assert.True(ServiceExists(name));

            File.Delete(driver);
            var message = WinDivertDoctor.RepairStaleService(serviceName: name);
            Assert.NotNull(message);
            Assert.Contains("Служба удалена", message);
            Assert.True(await Gone(name));
            Assert.Null(WinDivertDoctor.RepairStaleService(serviceName: name));

            // Своя пропавшая служба удаляется молча: о пропавшем файле говорит объяснение кода выхода.
            Assert.Equal(0, await Sc("create", name, "type=", "kernel", "start=", "demand", "binPath=", driver));
            Assert.Null(WinDivertDoctor.RepairStaleService(driver, name));
            Assert.True(await Gone(name));
        }
        finally
        {
            await Sc("delete", name);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Нет ли службы для диспетчера служб: этого ждёт RepairStaleService, и только это видит WinDivert. Ключ реестра
    /// диспетчер стирает чуть позже, поэтому его ждём отдельно.
    /// </summary>
    private static async Task<bool> Gone(string name)
    {
        if (await Sc("query", name) != 1060) return false;
        for (var i = 0; i < 50 && ServiceExists(name); i++) await Task.Delay(100);
        return !ServiceExists(name);
    }

    private static bool ServiceExists(string name) =>
        OperatingSystem.IsWindows() && Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name) is { } key && Dispose(key);

    private static bool Dispose(IDisposable d)
    {
        d.Dispose();
        return true;
    }

    private static async Task<int> Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var r = await ZapretSmart.Core.Share.ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(20), CancellationToken.None);
        return r.ExitCode;
    }
}
