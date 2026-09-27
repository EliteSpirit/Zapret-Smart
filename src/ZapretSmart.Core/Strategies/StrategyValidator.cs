using System.Text.RegularExpressions;

namespace ZapretSmart.Core.Strategies;

public readonly record struct ParsedArg(string Name, string? Value, EngineOption Option);

public static class StrategyValidator
{
    public const int MaxProfiles = 16;
    public const int MaxArgsPerProfile = 48;

    private static readonly Regex IdRe = new(@"^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex OptionNameRe = new(@"^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Validate(Strategy s)
    {
        var errors = new List<string>();

        if (!IdRe.IsMatch(s.Id ?? ""))
            errors.Add("id: допустимы только a-z, 0-9, '_' и '-', до 64 символов");
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 100)
            errors.Add("Название: обязательно, до 100 символов");
        if (s.Description is { Length: > 2000 })
            errors.Add("Описание: не длиннее 2000 символов");

        if (s.Intercept is null || (s.Intercept.Tcp is null && s.Intercept.Udp is null))
            errors.Add("Перехват: укажите порты TCP или UDP");
        else
        {
            if (s.Intercept.Tcp is not null && !EngineOptionCatalog.IsPortList(s.Intercept.Tcp))
                errors.Add($"Перехват TCP: некорректный список портов '{s.Intercept.Tcp}'");
            if (s.Intercept.Udp is not null && !EngineOptionCatalog.IsPortList(s.Intercept.Udp))
                errors.Add($"Перехват UDP: некорректный список портов '{s.Intercept.Udp}'");
        }

        if (s.Profiles is null || s.Profiles.Count == 0)
            errors.Add("Нужен хотя бы один профиль");
        else if (s.Profiles.Count > MaxProfiles)
            errors.Add($"Не больше {MaxProfiles} профилей");
        else
        {
            for (var i = 0; i < s.Profiles.Count; i++)
            {
                if (s.Profiles[i] is null) errors.Add($"Профиль {i + 1}: пустой");
                else ValidateProfile(s.Profiles[i], $"Профиль {i + 1}", errors);
            }
        }

        return errors;
    }

    public const int MaxListsPerProfile = 8;

    public static bool IsValidListId(string id) => IdRe.IsMatch(id);

    private static void ValidateListIds(IReadOnlyList<string>? ids, string path, List<string> errors)
    {
        if (ids is null)
            errors.Add($"{path}: должен быть массив имён");
        else if (ids.Count > MaxListsPerProfile)
            errors.Add($"{path}: не больше {MaxListsPerProfile}");
        else if (ids.Any(id => id is null || !IsValidListId(id)))
            errors.Add($"{path}: '{ids.First(id => id is null || !IsValidListId(id)) ?? "null"}' — ожидается имя списка (a-z, 0-9, '-', '_'), а не путь");
    }

    private static void ValidateProfile(Profile p, string path, List<string> errors)
    {
        ValidateListIds(p.Hostlists, $"{path}, списки доменов", errors);
        ValidateListIds(p.Ipsets, $"{path}, списки IP", errors);

        if (p.Args is null || p.Args.Count == 0)
        {
            errors.Add($"{path}: нет ни одной опции");
            return;
        }
        if (p.Args.Count > MaxArgsPerProfile)
        {
            errors.Add($"{path}: не больше {MaxArgsPerProfile} опций");
            return;
        }

        var hasDesync = false;
        for (var j = 0; j < p.Args.Count; j++)
        {
            if (TryParseArg(p.Args[j], out var arg, out var error))
                hasDesync |= arg.Name is "dpi-desync" or "dup" or "wssize" or "hostcase" or "hostspell" or "domcase" or "methodeol" or "hostnospace";
            else
                errors.Add($"{path}, строка {j + 1}: {error}");
        }
        if (!hasDesync)
            errors.Add($"{path}: профиль ничего не делает с трафиком (нет dpi-desync или модификаторов)");
    }

    public static bool TryParseArg(string raw, out ParsedArg arg, out string error)
    {
        arg = default;
        if (string.IsNullOrEmpty(raw))
        {
            error = "пустой аргумент";
            return false;
        }

        var eq = raw.IndexOf('=');
        var name = eq < 0 ? raw : raw[..eq];
        var value = eq < 0 ? null : raw[(eq + 1)..];

        if (!OptionNameRe.IsMatch(name))
        {
            error = $"'{Truncate(raw)}': имя опции без '--', только a-z, 0-9 и '-'";
            return false;
        }
        if (!EngineOptionCatalog.TryGet(name, out var option))
        {
            error = $"опция '{name}' не разрешена в стратегиях";
            return false;
        }
        if (!option.IsValid(value))
        {
            error = $"недопустимое значение для '{name}': '{Truncate(value ?? "<нет>")}'";
            return false;
        }

        arg = new ParsedArg(name, value, option);
        error = "";
        return true;
    }

    private static string Truncate(string s) => s.Length <= 64 ? s : s[..64] + "…";
}
