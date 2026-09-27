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
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                return new LoadedStrategy(path, null, [$"файл больше {MaxFileBytes / 1024} КБ"]);
            return Parse(File.ReadAllText(path), path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new LoadedStrategy(path, null, ["не удалось прочитать: " + e.Message]);
        }
    }

    public static IReadOnlyList<LoadedStrategy> LoadDirectory(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).Select(LoadFile).ToList()
            : [];

    /// <summary>
    /// Свои стратегии сохраняются и удаляются по id, поэтому имя файла обязано совпадать с id.
    /// Иначе после правки рядом появлялся бы второй файл с тем же id, а удаление не находило бы исходный.
    /// </summary>
    public static IReadOnlyList<LoadedStrategy> LoadUserDirectory(string dir) =>
        LoadDirectory(dir)
            .Select(l => l.IsValid && Path.GetFileNameWithoutExtension(l.Source) != l.Strategy!.Id
                ? l with { Strategy = null, Errors = [$"имя файла должно совпадать с id: {l.Strategy.Id}.json"] }
                : l)
            .ToList();
}
