using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Engine;

/// <param name="FakeDir">Каталог комплектных фейковых пакетов (*.bin).</param>
/// <param name="ListsDir">Каталог локальных списков доменов (&lt;id&gt;.txt).</param>
public sealed record EngineLayout(string FakeDir, string ListsDir);

public sealed class StrategyRejectedException(IReadOnlyList<string> errors)
    : Exception("Стратегия не прошла проверку: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static class EngineCommandBuilder
{
    /// <summary>
    /// Собирает argv для движка. Стратегия проверяется заново: вызывающий код не обязан ей доверять.
    /// Результат передаётся в ProcessStartInfo.ArgumentList, без склейки в строку и без оболочки.
    /// </summary>
    public static IReadOnlyList<string> Build(Strategy strategy, EngineLayout layout)
    {
        var errors = StrategyValidator.Validate(strategy).ToList();
        if (errors.Count > 0)
            throw new StrategyRejectedException(errors);

        var argv = new List<string>();
        if (strategy.Intercept.Tcp is not null) argv.Add("--wf-tcp=" + strategy.Intercept.Tcp);
        if (strategy.Intercept.Udp is not null) argv.Add("--wf-udp=" + strategy.Intercept.Udp);

        for (var i = 0; i < strategy.Profiles.Count; i++)
        {
            var profile = strategy.Profiles[i];
            if (i > 0) argv.Add("--new");

            if (profile.Hostlist is not null)
            {
                var list = Path.Combine(layout.ListsDir, profile.Hostlist + ".txt");
                if (!File.Exists(list))
                    errors.Add($"Профиль {i + 1}: нет списка доменов '{profile.Hostlist}'");
                argv.Add("--hostlist=" + list);
            }

            foreach (var raw in profile.Args)
            {
                StrategyValidator.TryParseArg(raw, out var arg, out _);
                var value = arg.Value;
                if (arg.Option.Kind == OptionKind.Blob && value is not null && EngineOptionCatalog.IsBundledFileName(value))
                {
                    var file = Path.Combine(layout.FakeDir, value);
                    if (!File.Exists(file))
                        errors.Add($"Профиль {i + 1}: нет комплектного файла '{value}'");
                    value = file;
                }
                argv.Add(value is null ? "--" + arg.Name : $"--{arg.Name}={value}");
            }
        }

        if (errors.Count > 0)
            throw new StrategyRejectedException(errors);
        return argv;
    }
}
