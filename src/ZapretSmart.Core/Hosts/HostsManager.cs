namespace ZapretSmart.Core.Hosts;

/// <param name="Origin">Откуда взяты записи: «свежий список», «последний скачанный» или «встроенный снимок».</param>
public sealed record LoadedHostsSource(HostsSource Source, string Origin);

/// <summary>
/// Блок Zapret Smart в системном файле hosts: загрузка списка, запись с резервной копией, удаление.
/// Записи берутся из списка Flowseal (MIT): адреса GitHub, Telegram и Discord на случай подмены DNS.
/// </summary>
public sealed class HostsManager(string hostsPath, string dataDir, string bundledSnapshot, HttpClient http)
{
    public const string SourceUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/hosts";

    public string HostsPath { get; } = hostsPath;
    public string BackupPath => Path.Combine(dataDir, "hosts.backup");
    public string CachePath => Path.Combine(dataDir, "hosts-source.txt");

    /// <summary>Свежий список из сети; если сеть или источник подвели, последний скачанный, а если его нет, встроенный снимок.</summary>
    public async Task<LoadedHostsSource> LoadSourceAsync(CancellationToken ct)
    {
        try
        {
            var text = await http.GetStringAsync(SourceUrl, ct);
            var parsed = HostsFile.Parse(text);
            if (parsed.Entries.Count > 0)
            {
                Directory.CreateDirectory(dataDir);
                await File.WriteAllTextAsync(CachePath, text, ct);
                return new LoadedHostsSource(parsed, "свежий список");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
        }
        if (File.Exists(CachePath))
        {
            var cached = HostsFile.Parse(await File.ReadAllTextAsync(CachePath, ct));
            if (cached.Entries.Count > 0) return new LoadedHostsSource(cached, "последний скачанный список");
        }
        return new LoadedHostsSource(HostsFile.Parse(await File.ReadAllTextAsync(bundledSnapshot, ct)), "встроенный снимок");
    }

    /// <summary>
    /// Латиница-1 переводит каждый байт в один символ и обратно без потерь. hosts бывает в ANSI (русские комментарии в cp1251),
    /// и чтение как UTF-8 испортило бы чужие строки при записи.
    /// </summary>
    private static readonly System.Text.Encoding Bytes = System.Text.Encoding.Latin1;

    public IReadOnlyList<HostsEntry> CurrentBlock() =>
        File.Exists(HostsPath) ? HostsFile.ReadBlock(File.ReadAllText(HostsPath, Bytes)) : [];

    /// <summary>Ставит записи в блок приложения. Пустой список убирает блок. Файл меняется, только если есть что менять.</summary>
    public HostsPlan Apply(IReadOnlyList<HostsEntry> entries)
    {
        var current = File.Exists(HostsPath) ? File.ReadAllText(HostsPath, Bytes) : "";
        var plan = HostsFile.Plan(current, entries);
        if (!plan.Changed) return plan;

        Directory.CreateDirectory(dataDir);
        if (File.Exists(HostsPath)) File.Copy(HostsPath, BackupPath, overwrite: true);
        // Временный файл рядом с hosts: замена на одном томе атомарна, и при сбое hosts не останется полупустым.
        var tmp = HostsPath + ".zapret-smart.tmp";
        File.WriteAllText(tmp, plan.Text, Bytes);
        File.Move(tmp, HostsPath, overwrite: true);
        return plan;
    }
}
