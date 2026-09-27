using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Engine;

/// <param name="FakeDir">Каталог комплектных фейковых пакетов (*.bin).</param>
/// <param name="ListsDir">Каталог списков доменов (&lt;id&gt;.txt), включая автосписок и исключения.</param>
/// <param name="IpsetsDir">Каталог списков IP (&lt;id&gt;.txt).</param>
public sealed record EngineLayout(string FakeDir, string ListsDir, string IpsetsDir)
{
    public const string AutoListId = "auto";
    public const string ExcludeListId = "exclude";

    public string HostlistPath(string id) => Path.Combine(ListsDir, id + ".txt");
    public string IpsetPath(string id) => Path.Combine(IpsetsDir, id + ".txt");
}

public sealed class StrategyRejectedException(IReadOnlyList<string> errors)
    : Exception("Стратегия не прошла проверку: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed record EngineCommand(IReadOnlyList<string> Argv, IReadOnlyList<string> Warnings);

public static class EngineCommandBuilder
{
    /// <summary>
    /// Движок считает профиль с пустыми include-листами профилем «для всех» (см. HostlistCheck_/IpsetCheck_).
    /// Эти записи никогда не совпадут с реальным трафиком, но делают коллекцию непустой: пустой или
    /// ещё не скачанный список сужает профиль до нуля, а не расширяет до всего трафика.
    /// </summary>
    public const string GuardDomain = "zapret-smart.invalid";
    public const string GuardIp = "192.0.2.0/32";

    /// <summary>
    /// Собирает argv для движка. Стратегия проверяется заново: вызывающий код не обязан ей доверять.
    /// Результат передаётся в ProcessStartInfo.ArgumentList, без склейки в строку и без оболочки.
    /// </summary>
    public static EngineCommand Build(Strategy strategy, EngineLayout layout)
    {
        var errors = StrategyValidator.Validate(strategy).ToList();
        if (errors.Count > 0)
            throw new StrategyRejectedException(errors);

        var warnings = new List<string>();
        var argv = new List<string>();
        if (strategy.Intercept.Tcp is not null) argv.Add("--wf-tcp=" + strategy.Intercept.Tcp);
        if (strategy.Intercept.Udp is not null) argv.Add("--wf-udp=" + strategy.Intercept.Udp);

        for (var i = 0; i < strategy.Profiles.Count; i++)
        {
            var profile = strategy.Profiles[i];
            var name = $"Профиль {i + 1}";
            if (i > 0) argv.Add("--new");

            foreach (var id in profile.Hostlists)
            {
                if (File.Exists(layout.HostlistPath(id)))
                    argv.Add("--hostlist=" + layout.HostlistPath(id));
                else
                    warnings.Add($"{name}: список доменов '{id}' ещё не загружен, профиль работает без него");
            }
            if (profile.AutoHostlist)
            {
                var auto = layout.HostlistPath(EngineLayout.AutoListId);
                if (!File.Exists(auto))
                {
                    Directory.CreateDirectory(layout.ListsDir);
                    File.WriteAllText(auto, "");
                }
                argv.Add("--hostlist-auto=" + auto);
            }
            if (profile.Hostlists.Count > 0 || profile.AutoHostlist)
            {
                argv.Add("--hostlist-domains=" + GuardDomain);
                if (File.Exists(layout.HostlistPath(EngineLayout.ExcludeListId)))
                    argv.Add("--hostlist-exclude=" + layout.HostlistPath(EngineLayout.ExcludeListId));
            }

            foreach (var id in profile.Ipsets)
            {
                if (File.Exists(layout.IpsetPath(id)))
                    argv.Add("--ipset=" + layout.IpsetPath(id));
                else
                    warnings.Add($"{name}: список IP '{id}' ещё не загружен, профиль работает без него");
            }
            if (profile.Ipsets.Count > 0)
                argv.Add("--ipset-ip=" + GuardIp);

            foreach (var raw in profile.Args)
            {
                StrategyValidator.TryParseArg(raw, out var arg, out _);
                var value = arg.Value;
                if (arg.Option.Kind == OptionKind.Blob && value is not null && EngineOptionCatalog.IsBundledFileName(value))
                {
                    var file = Path.Combine(layout.FakeDir, value);
                    if (!File.Exists(file))
                        errors.Add($"{name}: нет комплектного файла '{value}'");
                    value = file;
                }
                argv.Add(value is null ? "--" + arg.Name : $"--{arg.Name}={value}");
            }
        }

        if (errors.Count > 0)
            throw new StrategyRejectedException(errors);
        return new EngineCommand(argv, warnings);
    }
}
