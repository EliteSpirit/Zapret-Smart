using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Lists;

/// <summary>Что новая версия приложения поменяла во встроенном списке пользователя.</summary>
public sealed record BundledListUpdate(string ListId, int Added, int Removed);

public sealed class ListStore(EngineLayout layout, string bundledDir)
{
    public string Directory => layout.ListsDir;
    public string IpsetsDirectory => layout.IpsetsDir;

    /// <summary>Копии встроенных списков, с которых начинал пользователь: по ним видно, что поменялось в новой версии, а что поменял он сам.</summary>
    public string BaselineDirectory => Path.Combine(layout.ListsDir, ".bundled");

    /// <summary>
    /// Копирует встроенные списки, которых ещё нет у пользователя, вливает в уже скопированные изменения новой версии
    /// и создаёт пустые автосписок и исключения. Свои домены, удаления, порядок строк и комментарии пользователя сохраняются.
    /// </summary>
    public IReadOnlyList<BundledListUpdate> SeedMissing()
    {
        System.IO.Directory.CreateDirectory(layout.ListsDir);
        System.IO.Directory.CreateDirectory(layout.IpsetsDir);
        var updates = new List<BundledListUpdate>();
        if (System.IO.Directory.Exists(bundledDir))
        {
            System.IO.Directory.CreateDirectory(BaselineDirectory);
            foreach (var src in System.IO.Directory.EnumerateFiles(bundledDir, "*.txt"))
            {
                var name = Path.GetFileName(src);
                var dst = Path.Combine(layout.ListsDir, name);
                var baseline = Path.Combine(BaselineDirectory, name);
                if (!File.Exists(dst))
                {
                    File.Copy(src, dst);
                }
                else if (File.Exists(baseline))
                {
                    var update = MergeBundled(src, baseline, dst);
                    if (update is not null) updates.Add(update);
                }
                // Установки до 0.4.0 базы не хранили. Встроенные списки в них не менялись, поэтому база равна текущему файлу
                // из поставки, и заводим её молча: иначе снова появились бы домены, которые пользователь сам удалил.
                File.Copy(src, baseline, overwrite: true);
            }
        }
        foreach (var id in new[] { EngineLayout.AutoListId, EngineLayout.ExcludeListId })
            if (!File.Exists(layout.HostlistPath(id))) File.WriteAllText(layout.HostlistPath(id), "");
        return updates;
    }

    /// <summary>
    /// Трёхстороннее слияние: что добавила и убрала новая версия относительно базы, то и применяется к файлу пользователя.
    /// Домен, который пользователь удалил сам, обратно не появится: в новой версии он не «добавлен», он был и в базе.
    /// </summary>
    private static BundledListUpdate? MergeBundled(string bundled, string baseline, string user)
    {
        var fresh = Entries(File.ReadLines(bundled));
        var old = Entries(File.ReadLines(baseline));
        var added = fresh.Except(old).ToList();
        var removed = old.Except(fresh).ToHashSet();
        if (added.Count == 0 && removed.Count == 0) return null;

        var lines = File.ReadAllLines(user).ToList();
        var removedNow = lines.RemoveAll(l => removed.Contains(Normalize(l)));
        var present = Entries(lines).ToHashSet();
        var toAdd = added.Where(d => !present.Contains(d)).ToList();
        if (toAdd.Count > 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.AddRange(toAdd);
        }
        if (removedNow == 0 && toAdd.Count == 0) return null;

        var tmp = user + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, user, overwrite: true);
        return new BundledListUpdate(Path.GetFileNameWithoutExtension(user), toAdd.Count, removedNow);
    }

    private static string Normalize(string line) => line.Trim().ToLowerInvariant();

    private static IEnumerable<string> Entries(IEnumerable<string> lines) =>
        lines.Select(Normalize).Where(l => l.Length > 0 && !l.StartsWith('#')).Distinct();

    /// <summary>Списки доменов, которые можно выбрать в профиле: файлы плюс подписки, даже ещё не скачанные.</summary>
    public IReadOnlyList<string> HostlistIds() =>
        Ids(layout.ListsDir, ListKind.Domains).Where(id => id is not (EngineLayout.AutoListId or EngineLayout.ExcludeListId)).ToList();

    public IReadOnlyList<string> IpsetIds() => Ids(layout.IpsetsDir, ListKind.Ips);

    private static IReadOnlyList<string> Ids(string dir, ListKind kind) =>
        (System.IO.Directory.Exists(dir)
            ? System.IO.Directory.EnumerateFiles(dir, "*.txt").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            : [])
        .Concat(Subscriptions.All.Where(s => s.Kind == kind).Select(s => s.Id))
        .Where(StrategyValidator.IsValidListId)
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>Свой список доменов. Подписки и служебные списки так не перезаписать.</summary>
    public void WriteHostlist(string id, IEnumerable<string> domains)
    {
        if (!StrategyValidator.IsValidListId(id) || Subscriptions.IsSubscription(id) || id is EngineLayout.AutoListId or EngineLayout.ExcludeListId)
            throw new ArgumentException($"список '{id}' нельзя перезаписать", nameof(id));
        System.IO.Directory.CreateDirectory(layout.ListsDir);
        File.WriteAllLines(layout.HostlistPath(id), domains);
    }

    public int AutoListCount() =>
        File.Exists(layout.HostlistPath(EngineLayout.AutoListId))
            ? File.ReadLines(layout.HostlistPath(EngineLayout.AutoListId)).Count(l => l.Trim().Length > 0)
            : 0;

    public void ClearAutoList() => File.WriteAllText(layout.HostlistPath(EngineLayout.AutoListId), "");
}
