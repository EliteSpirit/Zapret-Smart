using System.Text.RegularExpressions;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Search;

/// <summary>Набор целей поиска для одного сервиса: сайт и его CDN.</summary>
public sealed record TargetGroup(string Id, string Name, string Description, IReadOnlyList<string> Targets);

/// <summary>
/// Цели поиска. Цель: домен («discord.com») или домен с путём к файлу («i.ytimg.com/vi/.../maxresdefault.jpg»).
/// Путь нужен там, где на «/» сервер отвечает крошечной страницей: блокировка, которая замораживает поток после ~16 КБ,
/// на ней не видна, а на настоящем файле видна. В hostlist движка идёт только домен.
/// </summary>
public static partial class SearchTargets
{
    /// <summary>
    /// Сервисы, у которых главная страница открывается, а видео, картинки или чат идут с других доменов и режутся
    /// отдельно. Стратегия, найденная только по главной, оставляла YouTube без видео и Discord без вложений.
    /// У CDN Discord и видео YouTube больших файлов без подписи нет: для них проверка показывает, пропускает ли DPI
    /// само имя сайта (SNI), и этого хватает, Discord блокируют именно так.
    /// </summary>
    public static readonly IReadOnlyList<TargetGroup> Groups =
    [
        new("youtube", "YouTube", "сайт, видео, обложки, аватары",
        [
            "www.youtube.com",
            "i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg",
            "yt3.ggpht.com",
            "redirector.googlevideo.com",
            "youtubei.googleapis.com",
        ]),
        new("discord", "Discord", "сайт, чат, вложения, медиа",
        [
            "discord.com",
            "gateway.discord.gg",
            "cdn.discordapp.com/embed/avatars/0.png",
            "media.discordapp.net",
            "discordapp.com",
        ]),
        new("rutracker", "rutracker", "сайт",
        [
            "rutracker.org",
        ]),
    ];

    /// <summary>Все цели всех сервисов: набор поиска по умолчанию.</summary>
    public static IReadOnlyList<string> Default => Groups.SelectMany(g => g.Targets).ToList();

    [GeneratedRegex(@"^[A-Za-z0-9/._~%-]*$")]
    private static partial Regex PathRe();

    /// <summary>Домен цели: то, что идёт в hostlist движка.</summary>
    public static string Host(string target)
    {
        var slash = target.IndexOf('/');
        return (slash < 0 ? target : target[..slash]).Trim().ToLowerInvariant();
    }

    /// <summary>Путь цели с ведущим «/»; для голого домена «/».</summary>
    public static string PathOf(string target)
    {
        var slash = target.IndexOf('/');
        return slash < 0 ? "/" : target[slash..].Trim();
    }

    /// <summary>Домен по правилам движка и путь из безопасных символов (без «?», «#», пробелов и «..»).</summary>
    public static bool IsValid(string target)
    {
        var path = PathOf(target);
        return EngineOptionCatalog.IsDomain(Host(target)) && PathRe().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// Цель в каноническом виде: домен в нижнем регистре, путь как есть. Путь чувствителен к регистру
    /// (у обложки YouTube «dQw4w9WgXcQ»), и в нижнем регистре сервер отдал бы 404.
    /// </summary>
    public static string Normalize(string target)
    {
        target = target.Trim();
        var slash = target.IndexOf('/');
        return slash < 0 ? target.ToLowerInvariant() : target[..slash].ToLowerInvariant() + target[slash..];
    }

    /// <summary>Цель для показа человеку: у адреса файла только домен, длинный путь в итогах ни к чему.</summary>
    public static string Display(string target) => target.Contains('/') ? Host(target) : target;

    /// <summary>Цели для показа через запятую, без повторов доменов.</summary>
    public static string DisplayList(IEnumerable<string> targets) => string.Join(", ", targets.Select(Display).Distinct());

    /// <summary>Домены целей без повторов: содержимое hostlist для поиска и для сохранённой стратегии.</summary>
    public static IReadOnlyList<string> Hosts(IEnumerable<string> targets) => targets.Select(Host).Distinct().ToList();

    /// <summary>Сервисы, все цели которых есть в списке.</summary>
    public static IReadOnlyList<string> SelectedGroups(IEnumerable<string> targets)
    {
        var set = targets.Select(Normalize).ToHashSet();
        return Groups.Where(g => g.Targets.All(set.Contains)).Select(g => g.Id).ToList();
    }
}
