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
