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

    /// <summary>Тема оформления. Неизвестный или пустой id означает тему по умолчанию.</summary>
    public string? ThemeId { get; init; }

    /// <summary>Анимировать фон темы (бинарный дождь, снег и т. п.). Выключено: фон неподвижен.</summary>
    public bool AnimatedBackdrop { get; init; } = true;

    /// <summary>Меню вкладок строкой сверху, а не колонкой слева: окну нужно меньше ширины.</summary>
    public bool MenuOnTop { get; init; }

    /// <summary>Свой блок в системном файле hosts. По умолчанию выключен: это изменение системного файла.</summary>
    public bool HostsEnabled { get; init; }

    /// <summary>Прокси для телефона в локальной сети: его соединения открывает ПК, и обход их обрабатывает. По умолчанию выключен.</summary>
    public bool ShareEnabled { get; init; }

    /// <summary>
    /// Включать раздачу при каждом запуске программы, даже если в прошлый раз её выключили. Без этой настройки
    /// раздача при запуске просто остаётся такой, какой была.
    /// </summary>
    public bool ShareAutoStart { get; init; }

    /// <summary>
    /// Писать в журнал раздачи адреса сайтов, которые открывают устройства. По умолчанию выключено: журнал тогда
    /// был бы историей посещений, а его присылают для разбора проблем.
    /// </summary>
    public bool ShareLogSites { get; init; }

    /// <summary>Стратегия движка раздачи. Пусто — та же, что выбрана для обхода ПК.</summary>
    public string? ShareStrategyId { get; init; }

    /// <summary>Порт прокси для телефона.</summary>
    public int SharePort { get; init; } = Share.ShareProxy.DefaultPort;
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
