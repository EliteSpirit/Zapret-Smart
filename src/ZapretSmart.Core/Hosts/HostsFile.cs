using System.Net;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Hosts;

public sealed record HostsEntry(IPAddress Address, string Host)
{
    public override string ToString() => $"{Address} {Host}";
}

/// <param name="Entries">Записи, которые можно ставить.</param>
/// <param name="Rejected">Строки источника, которые отброшены, с причиной.</param>
public sealed record HostsSource(IReadOnlyList<HostsEntry> Entries, IReadOnlyList<string> Rejected);

/// <param name="Text">Новый текст файла hosts.</param>
/// <param name="Applied">Записи, попавшие в блок приложения.</param>
/// <param name="ShadowedByUser">Имена, которые пользователь уже задал сам вне блока: их не трогаем, его запись главнее.</param>
public sealed record HostsPlan(string Text, IReadOnlyList<HostsEntry> Applied, IReadOnlyList<string> ShadowedByUser, bool Changed);

/// <summary>
/// Блок приложения в системном файле hosts. Всё вне меток остаётся как было, байт в байт.
/// Блок дописывается в конец: при совпадении имени Windows берёт первую запись, поэтому записи пользователя главнее.
/// </summary>
public static class HostsFile
{
    /// <summary>Только ASCII: hosts бывает в любой кодировке, а метки должны читаться в каждой.</summary>
    public const string BeginMarker = "# >>> Zapret Smart: managed block, edits inside it will be overwritten";
    public const string EndMarker = "# <<< Zapret Smart";

    /// <summary>Больше записей в «списке хостов» не бывает; всё сверх похоже на ошибку источника.</summary>
    public const int MaxEntries = 2000;

    /// <summary>
    /// Эти имена источник переопределить не может. Подмена адресов обновления Windows и проверки сети ломает систему,
    /// и Defender считает её атакой (HostsFileHijack). Остальное безопасно: запись в hosts меняет только адрес,
    /// а сертификат сайта браузер и приложение проверяют как обычно, так что подменить содержимое через неё нельзя.
    /// </summary>
    private static readonly string[] ProtectedSuffixes =
    [
        "microsoft.com", "windows.com", "windowsupdate.com", "update.microsoft.com", "msftconnecttest.com", "msftncsi.com",
        "localhost",
    ];

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    /// <summary>Разбирает источник в формате hosts: «IP имя [имя…]», комментарии через #.</summary>
    public static HostsSource Parse(string text)
    {
        var entries = new List<HostsEntry>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var ip))
            {
                rejected.Add($"{line}: нужен IP-адрес и имя");
                continue;
            }
            foreach (var name in parts.Skip(1))
            {
                var host = name.ToLowerInvariant().TrimEnd('.');
                if (!EngineOptionCatalog.IsDomain(host))
                    rejected.Add($"{name}: не доменное имя");
                else if (IsProtected(host))
                    rejected.Add($"{host}: служебное имя Windows или GitHub, переопределять нельзя");
                // У одного имени бывает несколько адресов (у discord.com в источнике четыре): склеиваем только точные повторы.
                else if (seen.Add(host + " " + ip))
                    entries.Add(new HostsEntry(ip, host));
            }
        }
        if (entries.Count > MaxEntries)
            return new HostsSource([], [$"в источнике {entries.Count} записей, больше {MaxEntries}: похоже на ошибку, ничего не применено"]);
        return new HostsSource(entries, rejected);
    }

    public static bool IsProtected(string host) =>
        ProtectedSuffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));

    /// <summary>Записи, которые сейчас стоят в блоке приложения.</summary>
    public static IReadOnlyList<HostsEntry> ReadBlock(string current) =>
        Parse(string.Join('\n', Split(current).Block ?? [])).Entries;

    /// <summary>Новый текст файла: блок приложения заменён записями <paramref name="entries"/>. Пустой список убирает блок.</summary>
    public static HostsPlan Plan(string current, IReadOnlyList<HostsEntry> entries)
    {
        var (before, block, after, newline) = Split(current);
        var hasBlock = block is not null;
        var outside = before.Concat(after).ToList();
        var userHosts = Parse(string.Join('\n', outside)).Entries.Select(e => e.Host).ToHashSet(StringComparer.Ordinal);
        var shadowed = entries.Where(e => userHosts.Contains(e.Host)).Select(e => e.Host).ToList();
        var applied = entries.Where(e => !userHosts.Contains(e.Host)).ToList();

        // Ставить нечего и блока нет: файл не трогаем вовсе, даже пробелы в конце.
        if (applied.Count == 0 && !hasBlock) return new HostsPlan(current, applied, shadowed, false);

        // Пустая строка перед блоком наша: мы её добавили, мы и убираем. Остальное пользователя остаётся как было.
        if (hasBlock && before.Count > 0 && before[^1].Length == 0) before.RemoveAt(before.Count - 1);
        var lines = before.Concat(after).ToList();
        if (applied.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(BeginMarker);
            lines.AddRange(applied.Select(e => e.ToString()));
            lines.Add(EndMarker);
        }
        var text = lines.Count == 0 ? "" : string.Join(newline, lines) + newline;
        return new HostsPlan(text, applied, shadowed, text != current);
    }

    /// <summary>Block равен null, если блока приложения в файле нет.</summary>
    private static (List<string> Before, List<string>? Block, List<string> After, string Newline) Split(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var begin = lines.FindIndex(l => l.Trim() == BeginMarker);
        if (begin < 0) return (lines, null, [], newline);
        var end = lines.FindIndex(begin + 1, l => l.Trim() == EndMarker);
        // Нет конечной метки (файл обрезали руками): блоком считаем всё до конца, иначе он размножался бы при каждом обновлении.
        if (end < 0) return (lines[..begin], lines[(begin + 1)..], [], newline);
        return (lines[..begin], lines[(begin + 1)..end], lines[(end + 1)..], newline);
    }

    private static string StripComment(string line)
    {
        var i = line.IndexOf('#');
        return i < 0 ? line : line[..i];
    }
}
