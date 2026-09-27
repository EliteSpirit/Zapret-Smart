using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretSmart.Core.Strategies;

public sealed record LoadedStrategy(string Source, Strategy? Strategy, IReadOnlyList<string> Errors)
{
    public bool IsValid => Strategy is not null && Errors.Count == 0;
}

public static class StrategyLoader
{
    public const long MaxFileBytes = 256 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static LoadedStrategy Parse(string json, string source)
    {
        Strategy? strategy;
        try
        {
            strategy = JsonSerializer.Deserialize<Strategy>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            return new LoadedStrategy(source, null, [$"JSON: {e.Message}"]);
        }
        if (strategy is null)
            return new LoadedStrategy(source, null, ["JSON: пустой документ"]);

        var errors = StrategyValidator.Validate(strategy);
        return new LoadedStrategy(source, errors.Count == 0 ? strategy : null, errors);
    }

    public static LoadedStrategy LoadFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            return new LoadedStrategy(path, null, [$"файл больше {MaxFileBytes / 1024} КБ"]);
        return Parse(File.ReadAllText(path), path);
    }

    public static IReadOnlyList<LoadedStrategy> LoadDirectory(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(LoadFile).ToList()
            : [];
}
