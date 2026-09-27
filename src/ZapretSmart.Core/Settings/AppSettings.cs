using System.Text.Json;

namespace ZapretSmart.Core.Settings;

public sealed record AppSettings
{
    /// <summary>
    /// Обмен стратегиями с сообществом. По умолчанию выключен.
    /// Включён: можно брать стратегии из базы, а стратегии, прошедшие автотест, отправляются в базу
    /// (ASN, субъект РФ, параметры стратегии, метрики теста). Хостлисты и логи не отправляются никогда.
    /// </summary>
    public bool CommunityEnabled { get; init; }

    public string? SelectedStrategyId { get; init; }

    /// <summary>Пока обход включён, периодически проверять сайты и переключать стратегию, если они перестали открываться.</summary>
    public bool WatchdogEnabled { get; init; } = true;

    /// <summary>Закрытие окна при включённом обходе прячет приложение в трей, а не выключает обход.</summary>
    public bool CloseToTray { get; init; } = true;
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZapretSmart", "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}
