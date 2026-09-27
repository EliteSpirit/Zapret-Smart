using System.Text.Json;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Storage;

public sealed class UserStrategyStore(string dir)
{
    public string Directory { get; } = dir;

    public string PathFor(string id) => Path.Combine(Directory, id + ".json");

    public static string NewId() => "user-" + Guid.NewGuid().ToString("N")[..8];

    public void Save(Strategy strategy)
    {
        var errors = StrategyValidator.Validate(strategy);
        if (errors.Count > 0) throw new StrategyRejectedException(errors);

        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(strategy.Id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(strategy, StrategyLoader.JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string id)
    {
        if (!StrategyValidator.IsValidListId(id)) throw new ArgumentException("некорректный id", nameof(id));
        File.Delete(PathFor(id));
    }
}

public sealed class ListStore(string userDir, string bundledDir)
{
    public string Directory { get; } = userDir;

    /// <summary>Копирует встроенные списки, которых ещё нет у пользователя. Правки пользователя не перезаписываются.</summary>
    public void SeedMissing()
    {
        if (!System.IO.Directory.Exists(bundledDir)) return;
        System.IO.Directory.CreateDirectory(Directory);
        foreach (var src in System.IO.Directory.EnumerateFiles(bundledDir, "*.txt"))
        {
            var dst = Path.Combine(Directory, Path.GetFileName(src));
            if (!File.Exists(dst)) File.Copy(src, dst);
        }
    }

    public IReadOnlyList<string> Ids() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.EnumerateFiles(Directory, "*.txt")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(StrategyValidator.IsValidListId)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
}
