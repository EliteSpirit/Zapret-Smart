using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using ZapretSmart.Core.Updates;

namespace ZapretSmart.Core.Tests;

public sealed class UpdatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zs-upd-" + Guid.NewGuid().ToString("N"));

    public UpdatesTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("0.3.0", "0.2.0", 1)]
    [InlineData("v0.10.0", "0.9.9", 1)]
    [InlineData("0.3.0-rc.1", "0.3.0", -1)]
    [InlineData("0.3.0+abc", "0.3.0", 0)]
    [InlineData("0.3.0-rc.2", "0.3.0-rc.1", 1)]
    public void VersionsCompareLikeReleases(string a, string b, int sign)
    {
        Assert.True(AppVersion.TryParse(a, out var va));
        Assert.True(AppVersion.TryParse(b, out var vb));
        Assert.Equal(sign, Math.Sign(va.CompareTo(vb)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.3")]
    [InlineData("0.3.x")]
    [InlineData("0.3.0-")]
    public void BadVersionsAreRejected(string text) => Assert.False(AppVersion.TryParse(text, out _));

    private const string Base = "https://github.com/EliteSpirit/Zapret-Smart/releases/download";

    private static string Release(string tag, bool prerelease = true, bool draft = false, bool withSums = true, string? zipUrl = null)
    {
        var v = tag.TrimStart('v');
        var sums = withSums ? $$""",{ "name": "SHA256SUMS.txt", "browser_download_url": "{{Base}}/{{tag}}/SHA256SUMS.txt" }""" : "";
        return $$"""
            { "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
              "published_at": "2026-09-27T13:00:00Z",
              "assets": [ { "name": "ZapretSmart-{{v}}-win-x64.zip", "browser_download_url": "{{zipUrl ?? $"{Base}/{tag}/ZapretSmart-{v}-win-x64.zip"}}" }{{sums}} ] }
            """;
    }

    [Fact]
    public void OnlyInstallableReleasesAreListedNewestFirst()
    {
        var json = "[" + string.Join(",",
            Release("v0.2.0"),
            Release("v0.3.0"),
            Release("v0.4.0", draft: true),
            Release("v0.1.0", withSums: false),
            Release("v0.3.1", zipUrl: "https://evil.example/ZapretSmart-0.3.1-win-x64.zip"),
            Release("not-a-version")) + "]";
        var list = Releases.Parse(json);
        Assert.Equal(["0.3.0", "0.2.0"], list.Select(r => r.Version.ToString()));
        Assert.Equal("ZapretSmart-0.3.0-win-x64.zip", list[0].ZipName);
    }

    [Fact]
    public void ExpectedHashIsFoundOnlyForExactFile()
    {
        var hash = new string('a', 64);
        var sums = $"{hash}  ZapretSmart-0.3.0-win-x64.zip\n{new string('b', 64)} *other.zip\nbroken line\n";
        Assert.Equal(hash, UpdateInstaller.ExpectedHash(sums, "ZapretSmart-0.3.0-win-x64.zip"));
        Assert.Equal(new string('b', 64), UpdateInstaller.ExpectedHash(sums, "other.zip"));
        Assert.Null(UpdateInstaller.ExpectedHash(sums, "ZapretSmart-0.3.0-win-x64.zip.bak"));
    }

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open()))
                    w.Write(text);
        return ms.ToArray();
    }

    private ReleaseInfo Info() => new(new AppVersion(0, 3, 0, null), "v0.3.0", true, DateTimeOffset.UtcNow,
        "ZapretSmart-0.3.0-win-x64.zip", $"{Base}/v0.3.0/ZapretSmart-0.3.0-win-x64.zip", $"{Base}/v0.3.0/SHA256SUMS.txt");

    private UpdateInstaller Installer(byte[] zip, string? sumsOverride = null)
    {
        var hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var handler = new MapHandler(new Dictionary<string, byte[]>
        {
            [$"{Base}/v0.3.0/SHA256SUMS.txt"] = System.Text.Encoding.UTF8.GetBytes(sumsOverride ?? $"{hash}  ZapretSmart-0.3.0-win-x64.zip\n"),
            [$"{Base}/v0.3.0/ZapretSmart-0.3.0-win-x64.zip"] = zip,
        });
        return new UpdateInstaller(new HttpClient(handler), _dir);
    }

    [Fact]
    public async Task VerifiedArchiveIsExtracted()
    {
        var files = await Installer(Zip(("ZapretSmart.exe", "exe"), ("engine/winws.exe", "engine"))).DownloadAsync(Info(), null, CancellationToken.None);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(files, "ZapretSmart.exe")));
        Assert.Equal("engine", File.ReadAllText(Path.Combine(files, "engine", "winws.exe")));
    }

    [Fact]
    public async Task WrongChecksumInstallsNothing()
    {
        var installer = Installer(Zip(("ZapretSmart.exe", "exe")), $"{new string('0', 64)}  ZapretSmart-0.3.0-win-x64.zip\n");
        var e = await Assert.ThrowsAsync<UpdateException>(() => installer.DownloadAsync(Info(), null, CancellationToken.None));
        Assert.Contains("контрольная сумма", e.Message);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.zip", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_dir, "ZapretSmart.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ArchiveCannotWriteOutsideItsFolder()
    {
        var installer = Installer(Zip(("ZapretSmart.exe", "exe"), ("../../escaped.txt", "x")));
        await Assert.ThrowsAsync<IOException>(() => installer.DownloadAsync(Info(), null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "escaped.txt")));
    }

    private static string? PowerShell()
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "powershell.exe" } : ["pwsh"])
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    public sealed class PowerShellFactAttribute : FactAttribute
    {
        public PowerShellFactAttribute()
        {
            if (PowerShell() is null) Skip = "нет PowerShell";
        }
    }

    private (string Source, string Target, string Log) ScriptSetup()
    {
        var source = Path.Combine(_dir, "update", "files");
        var target = Path.Combine(_dir, "app");
        Directory.CreateDirectory(Path.Combine(source, "engine"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "ZapretSmart.dll"), "new dll");
        File.WriteAllText(Path.Combine(source, "same.txt"), "same");
        File.WriteAllText(Path.Combine(source, "engine", "added.bin"), "added");
        File.WriteAllText(Path.Combine(target, "ZapretSmart.dll"), "old dll");
        File.WriteAllText(Path.Combine(target, "same.txt"), "same");
        File.WriteAllText(Path.Combine(target, "user-note.txt"), "не трогать");
        return (source, target, Path.Combine(_dir, "data", "update.log"));
    }

    private static void RunScript(string script, string source, string target, string log, int retries)
    {
        // Процесс «приложения», которого скрипт ждёт: уже завершённый.
        using var finished = Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false }
            : new ProcessStartInfo("true") { UseShellExecute = false })!;
        finished.WaitForExit();
        var psi = new ProcessStartInfo(PowerShell()!) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        // Тест запускается из шага CI в PowerShell 7: как и приложение, изолируем Windows PowerShell от его модулей.
        UpdateInstaller.IsolateFromPowerShell7(psi);
        var manifest = UpdateInstaller.WriteManifest(source);
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-ProcessId", finished.Id.ToString(),
                     "-Source", source, "-Manifest", manifest, "-Target", target, "-Log", log, "-Retries", retries.ToString(), "-NoStart" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), "скрипт завис");
        Assert.True(p.ExitCode == 0, err);
    }

    private string WriteScript()
    {
        var script = Path.Combine(_dir, "apply-update.ps1");
        File.WriteAllText(script, UpdateInstaller.ApplyScript, new System.Text.UTF8Encoding(true));
        return script;
    }

    [PowerShellFact]
    public void ScriptReplacesChangedFilesAndKeepsOthers()
    {
        var (source, target, log) = ScriptSetup();
        RunScript(WriteScript(), source, target, log, retries: 3);

        var journal = File.Exists(log) ? File.ReadAllText(log) : "(журнала нет)";
        Assert.True(journal.Contains("ok: заменено файлов 2", StringComparison.Ordinal), journal);
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(target, "ZapretSmart.dll")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(target, "engine", "added.bin")));
        Assert.Equal("не трогать", File.ReadAllText(Path.Combine(target, "user-note.txt")));
        Assert.Contains("ok: заменено файлов 2", File.ReadAllText(log));
        Assert.Equal("old dll", File.ReadAllText(Path.Combine(_dir, "update", "backup", "ZapretSmart.dll")));
    }

    [PowerShellFact]
    public void ScriptRollsBackWhenAFileCannotBeReplaced()
    {
        if (!OperatingSystem.IsWindows()) return; // держать файл так, чтобы копирование упало, умеет только Windows
        var (source, target, log) = ScriptSetup();
        File.WriteAllText(Path.Combine(source, "zz-locked.exe"), "new exe");
        File.WriteAllText(Path.Combine(target, "zz-locked.exe"), "old exe");
        // Читать можно (хэш и резервная копия получатся), перезаписать нельзя: так ведёт себя exe, который ещё не отпустили.
        using (new FileStream(Path.Combine(target, "zz-locked.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            RunScript(WriteScript(), source, target, log, retries: 1);

        // Ошибка должна быть именно на занятом файле, а не общим провалом скрипта до начала замены.
        Assert.Contains("zz-locked.exe", File.ReadAllText(log));
        Assert.Contains("ошибка", File.ReadAllText(log));
        Assert.Equal("old dll", File.ReadAllText(Path.Combine(target, "ZapretSmart.dll")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(target, "zz-locked.exe")));
        Assert.False(File.Exists(Path.Combine(target, "engine", "added.bin")));
    }

    private sealed class MapHandler(Dictionary<string, byte[]> map) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(map.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
