using System.Net;
using System.Text;
using ZapretSmart.Core.Hosts;

namespace ZapretSmart.Core.Tests;

public sealed class HostsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zs-hosts-" + Guid.NewGuid().ToString("N"));

    private static HostsEntry E(string ip, string host) => new(IPAddress.Parse(ip), host);

    private const string WindowsDefault =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n# localhost name resolution is handled within DNS itself.\r\n#\t127.0.0.1       localhost\r\n";

    [Fact]
    public void SourceIsValidatedAndProtectedNamesAreRejected()
    {
        var s = HostsFile.Parse("""
            # комментарий
            140.82.121.3 github.com
            2606:50c0:8000::154 raw.githubusercontent.com
            162.159.138.232 discord.com
            162.159.137.232 discord.com
            162.159.137.232 discord.com
            1.2.3.4 Telegram.ORG.   # хвост
            1.2.3.4 update.microsoft.com
            1.2.3.4 localhost
            not-an-ip example.com
            1.2.3.4 bad_name
            5.6.7.8
            """);
        Assert.Equal(
            ["140.82.121.3 github.com", "2606:50c0:8000::154 raw.githubusercontent.com", "162.159.138.232 discord.com", "162.159.137.232 discord.com", "1.2.3.4 telegram.org"],
            s.Entries.Select(e => e.ToString()));
        Assert.Equal(5, s.Rejected.Count);
        Assert.Contains(s.Rejected, r => r.StartsWith("update.microsoft.com", StringComparison.Ordinal));
    }

    [Fact]
    public void TooLargeSourceIsRejectedEntirely()
    {
        var text = string.Join('\n', Enumerable.Range(0, HostsFile.MaxEntries + 1).Select(i => $"1.2.3.4 h{i}.example.com"));
        var s = HostsFile.Parse(text);
        Assert.Empty(s.Entries);
        Assert.Single(s.Rejected);
    }

    [Fact]
    public void BlockIsAppendedAndUserTextIsKeptByteForByte()
    {
        var plan = HostsFile.Plan(WindowsDefault, [E("140.82.121.3", "github.com")]);
        Assert.True(plan.Changed);
        Assert.StartsWith(WindowsDefault, plan.Text, StringComparison.Ordinal);
        Assert.EndsWith($"\r\n{HostsFile.BeginMarker}\r\n140.82.121.3 github.com\r\n{HostsFile.EndMarker}\r\n", plan.Text);

        // Повторная установка тех же записей ничего не меняет, удаление возвращает исходный текст до байта.
        Assert.False(HostsFile.Plan(plan.Text, [E("140.82.121.3", "github.com")]).Changed);
        Assert.Equal(WindowsDefault, HostsFile.Plan(plan.Text, []).Text);
    }

    [Fact]
    public void NothingToApplyLeavesFileUntouchedEvenTrailingBlankLines()
    {
        const string text = "1.1.1.1 my.host\n\n\n";
        var plan = HostsFile.Plan(text, []);
        Assert.False(plan.Changed);
        Assert.Equal(text, plan.Text);
    }

    [Fact]
    public void UserEntriesWinAndLinesAfterBlockSurvive()
    {
        var withBlock = HostsFile.Plan("10.0.0.1 discord.com\n", [E("162.159.138.232", "discord.com"), E("1.2.3.4", "telegram.org")]).Text;
        var plan = HostsFile.Plan(withBlock + "10.0.0.2 added.later\n", [E("1.2.3.4", "telegram.org"), E("5.6.7.8", "added.later")]);
        Assert.Equal(["added.later"], plan.ShadowedByUser);
        Assert.Equal(["1.2.3.4 telegram.org"], plan.Applied.Select(e => e.ToString()));
        Assert.StartsWith("10.0.0.1 discord.com\n10.0.0.2 added.later\n", plan.Text, StringComparison.Ordinal);
        Assert.Equal(["1.2.3.4 telegram.org"], HostsFile.ReadBlock(plan.Text).Select(e => e.ToString()));
    }

    [Fact]
    public void BlockWithoutEndMarkerIsReplacedNotDuplicated()
    {
        var broken = $"1.1.1.1 mine\n\n{HostsFile.BeginMarker}\n9.9.9.9 old.one\n";
        var plan = HostsFile.Plan(broken, [E("2.2.2.2", "new.one")]);
        Assert.Equal(1, plan.Text.Split(HostsFile.BeginMarker).Length - 1);
        Assert.DoesNotContain("old.one", plan.Text);
        Assert.StartsWith("1.1.1.1 mine\n", plan.Text, StringComparison.Ordinal);
    }

    private HostsManager Manager(HttpMessageHandler handler, string? snapshot = null)
    {
        Directory.CreateDirectory(_dir);
        var bundled = Path.Combine(_dir, "bundled.hosts");
        File.WriteAllText(bundled, snapshot ?? "140.82.121.3 github.com\n");
        return new HostsManager(Path.Combine(_dir, "etc-hosts"), Path.Combine(_dir, "data"), bundled, new HttpClient(handler));
    }

    [Fact]
    public void ApplyKeepsAnsiBytesAndMakesBackup()
    {
        var m = Manager(new StubHandler(HttpStatusCode.OK, ""));
        // «# мой» в cp1251: байты, которые в UTF-8 невалидны.
        var original = new byte[] { (byte)'#', (byte)' ', 0xEC, 0xEE, 0xE9, (byte)'\r', (byte)'\n', (byte)'1', (byte)'.', (byte)'1', (byte)'.', (byte)'1', (byte)'.', (byte)'1', (byte)' ', (byte)'a', (byte)'.', (byte)'b', (byte)'\r', (byte)'\n' };
        File.WriteAllBytes(m.HostsPath, original);

        var plan = m.Apply([E("140.82.121.3", "github.com")]);
        Assert.True(plan.Changed);
        var written = File.ReadAllBytes(m.HostsPath);
        Assert.Equal(original, written[..original.Length]);
        Assert.Equal(original, File.ReadAllBytes(m.BackupPath));
        Assert.Equal(["140.82.121.3 github.com"], m.CurrentBlock().Select(e => e.ToString()));

        m.Apply([]);
        Assert.Equal(original, File.ReadAllBytes(m.HostsPath));
    }

    [Fact]
    public async Task SourceFallsBackToCacheThenSnapshot()
    {
        var ok = Manager(new StubHandler(HttpStatusCode.OK, "149.154.167.220 telegram.org\n"));
        var fresh = await ok.LoadSourceAsync(CancellationToken.None);
        Assert.Equal("свежий список", fresh.Origin);
        Assert.Equal(["149.154.167.220 telegram.org"], fresh.Source.Entries.Select(e => e.ToString()));

        var down = Manager(new StubHandler(HttpStatusCode.ServiceUnavailable, ""));
        var cached = await down.LoadSourceAsync(CancellationToken.None);
        Assert.Equal("последний скачанный список", cached.Origin);

        File.Delete(down.CachePath);
        var snapshot = await down.LoadSourceAsync(CancellationToken.None);
        Assert.Equal("встроенный снимок", snapshot.Origin);
        Assert.Equal(["140.82.121.3 github.com"], snapshot.Source.Entries.Select(e => e.ToString()));
    }

    [Fact]
    public async Task GarbageFromNetworkDoesNotReplaceCache()
    {
        var ok = Manager(new StubHandler(HttpStatusCode.OK, "149.154.167.220 telegram.org\n"));
        await ok.LoadSourceAsync(CancellationToken.None);
        // Вместо списка пришла страница-заглушка провайдера: записей в ней нет, кэш остаётся прежним.
        var stub = Manager(new StubHandler(HttpStatusCode.OK, "<html>Доступ ограничен</html>"));
        var loaded = await stub.LoadSourceAsync(CancellationToken.None);
        Assert.Equal("последний скачанный список", loaded.Origin);
        Assert.Contains("telegram.org", File.ReadAllText(stub.CachePath));
    }

    [Fact]
    public void BundledSnapshotParsesCleanly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "hosts", "flowseal.hosts");
        var s = HostsFile.Parse(File.ReadAllText(path));
        Assert.Empty(s.Rejected);
        Assert.Contains(s.Entries, e => e.Host == "github.com");
        Assert.Contains(s.Entries, e => e.Host == "discord.com");
        Assert.Contains(s.Entries, e => e.Host == "telegram.org");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
