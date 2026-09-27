using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Lists;

public sealed class ListStore(EngineLayout layout, string bundledDir)
{
    public string Directory => layout.ListsDir;
    public string IpsetsDirectory => layout.IpsetsDir;

    /// <summary>
    /// Копирует встроенные списки, которых ещё нет у пользователя, и создаёт пустые автосписок и исключения.
    /// Правки пользователя не перезаписываются.
    /// </summary>
    public void SeedMissing()
    {
        System.IO.Directory.CreateDirectory(layout.ListsDir);
        System.IO.Directory.CreateDirectory(layout.IpsetsDir);
        if (System.IO.Directory.Exists(bundledDir))
        {
            foreach (var src in System.IO.Directory.EnumerateFiles(bundledDir, "*.txt"))
            {
                var dst = Path.Combine(layout.ListsDir, Path.GetFileName(src));
                if (!File.Exists(dst)) File.Copy(src, dst);
            }
        }
        foreach (var id in new[] { EngineLayout.AutoListId, EngineLayout.ExcludeListId })
            if (!File.Exists(layout.HostlistPath(id))) File.WriteAllText(layout.HostlistPath(id), "");
    }

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
