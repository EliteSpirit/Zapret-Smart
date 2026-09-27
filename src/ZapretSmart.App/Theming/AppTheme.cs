using Avalonia.Media;

namespace ZapretSmart.App.Theming;

/// <summary>Фон за окном: рисуется под содержимым и виден в просветах и сквозь полупрозрачные панели.</summary>
public enum BackdropKind
{
    None,
    BinaryRain,
    Halloween,
    Snow,
    Synthwave,
}

public sealed record ThemePalette(
    Color Background,
    Color Surface,
    Color SurfaceRaised,
    Color Border,
    Color Text,
    Color TextMuted,
    Color Accent,
    Color AccentText,
    Color Danger,
    Color Warning)
{
    public static Color Hex(string hex) => Color.Parse(hex);
}

/// <param name="PanelOpacity">Непрозрачность карточек и боковой панели. Меньше единицы только у тем с фоном, чтобы он просвечивал.</param>
/// <param name="Effect">Основной цвет фонового эффекта.</param>
public sealed record AppTheme(
    string Id,
    string Name,
    string Tagline,
    bool IsLight,
    ThemePalette Palette,
    BackdropKind Backdrop,
    Color Effect,
    bool Monospace,
    double CardRadius,
    double ControlRadius,
    double PanelOpacity)
{
    public bool HasBackdrop => Backdrop != BackdropKind.None;

    public static AppTheme Graphite { get; } = new(
        "graphite", "Графит", "Тёмная и спокойная, по умолчанию", false,
        new ThemePalette(
            Hex("#0F1115"), Hex("#171A21"), Hex("#1F232C"), Hex("#2A2F3A"),
            Hex("#E6E8EE"), Hex("#8E95A5"), Hex("#3DD6B5"), Hex("#062A22"),
            Hex("#FF6B6B"), Hex("#F5B94A")),
        BackdropKind.None, Hex("#3DD6B5"), false, 12, 8, 1);

    public static AppTheme Light { get; } = new(
        "light", "Светлая", "Для светлой комнаты и работы днём", true,
        new ThemePalette(
            Hex("#F3F4F6"), Hex("#FFFFFF"), Hex("#ECEEF2"), Hex("#D9DCE2"),
            Hex("#16181D"), Hex("#5A6170"), Hex("#0B7A65"), Hex("#FFFFFF"),
            Hex("#C42B2B"), Hex("#8F5B00")),
        BackdropKind.None, Hex("#0B7A65"), false, 12, 8, 1);

    public static AppTheme Hacker { get; } = new(
        "hacker", "Хакер", "Зелёный терминал и бинарный дождь", false,
        new ThemePalette(
            Hex("#020604"), Hex("#06100A"), Hex("#0B1A10"), Hex("#14331D"),
            Hex("#C2FFD2"), Hex("#5FBF7A"), Hex("#19FF6A"), Hex("#00190A"),
            Hex("#FF5C5C"), Hex("#E8FF5A")),
        BackdropKind.BinaryRain, Hex("#19FF6A"), true, 4, 3, 0.94);

    public static AppTheme Halloween { get; } = new(
        "halloween", "Хэллоуин", "Тыквенный оранжевый, луна, искры и летучие мыши", false,
        new ThemePalette(
            Hex("#0D0812"), Hex("#170F1E"), Hex("#22162C"), Hex("#36223F"),
            Hex("#F5EBFB"), Hex("#AE98BE"), Hex("#FF7A1A"), Hex("#2A1000"),
            Hex("#FF5C7A"), Hex("#FFC53D")),
        BackdropKind.Halloween, Hex("#FF8A2E"), false, 14, 9, 0.94);

    public static AppTheme Winter { get; } = new(
        "winter", "Зима", "Холодный синий и снегопад", false,
        new ThemePalette(
            Hex("#0A111E"), Hex("#101A2B"), Hex("#172338"), Hex("#243450"),
            Hex("#EAF2FF"), Hex("#93A7C4"), Hex("#7FC8FF"), Hex("#05233B"),
            Hex("#FF7A8A"), Hex("#FFD36B")),
        BackdropKind.Snow, Hex("#FFFFFF"), false, 16, 10, 0.94);

    public static AppTheme Synthwave { get; } = new(
        "synthwave", "Ретровейв", "Неон, закатное солнце и бегущая сетка", false,
        new ThemePalette(
            Hex("#0C0320"), Hex("#150630"), Hex("#200C44"), Hex("#34175F"),
            Hex("#F8EDFF"), Hex("#B9A2DE"), Hex("#FF4FB1"), Hex("#2B0019"),
            Hex("#FF6B6B"), Hex("#FFD84D")),
        BackdropKind.Synthwave, Hex("#24E5FF"), false, 12, 8, 0.94);

    public static IReadOnlyList<AppTheme> All { get; } = [Graphite, Light, Hacker, Halloween, Winter, Synthwave];

    public static AppTheme Default => Graphite;

    /// <summary>Неизвестный id (тема из будущей версии, опечатка в settings.json) даёт тему по умолчанию.</summary>
    public static AppTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;

    private static Color Hex(string hex) => ThemePalette.Hex(hex);
}
