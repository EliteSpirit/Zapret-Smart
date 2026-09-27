using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Search;

/// <summary>Параметры TLS-профиля (tcp/443), которые проверяются как одна стратегия.</summary>
public sealed record Candidate(string Label, IReadOnlyList<string> Args)
{
    /// <summary>Ключ не зависит от порядка опций: With() переносит заменённую опцию в конец, а движку порядок здесь не важен.</summary>
    public string Key => string.Join(' ', Args.Order(StringComparer.Ordinal));

    public string? Get(string option) =>
        Args.Select(a => a.Split('=', 2)).FirstOrDefault(p => p[0] == option) is { } p ? (p.Length > 1 ? p[1] : "") : null;

    /// <summary>Заменяет опцию (или добавляет в конец). Пустая строка — флаг без значения, null — удалить.</summary>
    public Candidate With(string option, string? value)
    {
        var args = Args.Where(a => a.Split('=', 2)[0] != option).ToList();
        if (value is not null) args.Add(value.Length == 0 ? option : $"{option}={value}");
        return this with { Args = args };
    }

    public Candidate Named(string suffix) => this with { Label = $"{Label}; {suffix}" };
}

public static class CandidateGenerator
{
    public const string GoogleTls = "tls_clienthello_www_google_com.bin";

    private static readonly string[] Foolings = ["badseq", "md5sig", "ts", "datanoack", "badsum"];

    /// <summary>
    /// Первый этап: по одному представителю каждого семейства техник.
    /// Порядок — от чаще срабатывающих у российских провайдеров к редким: при ограничении по времени важен.
    /// </summary>
    public static IReadOnlyList<Candidate> Explore()
    {
        var list = new List<Candidate>
        {
            C("multisplit + seqovl", "dpi-desync=multisplit", "dpi-desync-split-pos=1", "dpi-desync-split-seqovl=681", $"dpi-desync-split-seqovl-pattern={GoogleTls}"),
        };
        foreach (var f in Foolings)
        {
            list.Add(C($"fake + multidisorder, {f}", "dpi-desync=fake,multidisorder", "dpi-desync-split-pos=1,midsld", $"dpi-desync-fooling={f}", "dpi-desync-repeats=6", $"dpi-desync-fake-tls={GoogleTls}"));
            list.Add(C($"fake + multisplit, {f}", "dpi-desync=fake,multisplit", "dpi-desync-split-pos=1,midsld", $"dpi-desync-fooling={f}", "dpi-desync-repeats=6", $"dpi-desync-fake-tls={GoogleTls}"));
        }
        list.AddRange(
        [
            C("multidisorder", "dpi-desync=multidisorder", "dpi-desync-split-pos=1,midsld"),
            C("multisplit", "dpi-desync=multisplit", "dpi-desync-split-pos=1,midsld"),
            C("multisplit по sniext", "dpi-desync=multisplit", "dpi-desync-split-pos=sniext+1"),
            C("fake + multidisorder, autottl", "dpi-desync=fake,multidisorder", "dpi-desync-split-pos=1,midsld", "dpi-desync-autottl", "dpi-desync-repeats=6", $"dpi-desync-fake-tls={GoogleTls}"),
            C("fake + multisplit, autottl", "dpi-desync=fake,multisplit", "dpi-desync-split-pos=1,midsld", "dpi-desync-autottl", "dpi-desync-repeats=6", $"dpi-desync-fake-tls={GoogleTls}"),
            C("fake, autottl", "dpi-desync=fake", "dpi-desync-autottl", "dpi-desync-repeats=6", $"dpi-desync-fake-tls={GoogleTls}"),
            C("fake, rnd-sni, badseq", "dpi-desync=fake", "dpi-desync-fooling=badseq", "dpi-desync-repeats=6", "dpi-desync-fake-tls=!", "dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com"),
            C("fakedsplit, badseq", "dpi-desync=fakedsplit", "dpi-desync-split-pos=midsld", "dpi-desync-fooling=badseq"),
            C("fakeddisorder, badseq", "dpi-desync=fakeddisorder", "dpi-desync-split-pos=midsld", "dpi-desync-fooling=badseq"),
            C("hostfakesplit, badseq", "dpi-desync=hostfakesplit", "dpi-desync-fooling=badseq"),
        ]);
        foreach (var f in Foolings)
            list.Add(C($"fake, {f}", "dpi-desync=fake", $"dpi-desync-fooling={f}", "dpi-desync-repeats=6", $"dpi-desync-fake-tls={GoogleTls}"));
        return list;
    }

    /// <summary>Второй этап: меняем по одному параметру у найденного рабочего кандидата.</summary>
    public static IEnumerable<Candidate> Refine(Candidate seed)
    {
        var modes = seed.Get("dpi-desync") ?? "";
        var usesFake = modes.Split(',').Any(m => m is "fake" or "fakedsplit" or "fakeddisorder" or "hostfakesplit");
        var usesSplit = seed.Get("dpi-desync-split-pos") is not null;

        if (usesSplit)
            foreach (var pos in new[] { "1", "2", "midsld", "1,midsld", "2,midsld", "sniext+1", "1,sniext+1,host+1,midsld" })
                yield return seed.With("dpi-desync-split-pos", pos).Named($"разрез {pos}");

        if (seed.Get("dpi-desync-split-seqovl") is not null)
        {
            foreach (var ovl in new[] { "1", "336", "568" })
                yield return seed.With("dpi-desync-split-seqovl", ovl).Named($"seqovl {ovl}");
            yield return seed.With("dpi-desync-split-seqovl-pattern", "tls_clienthello_vk_com.bin").Named("seqovl-шаблон vk.com");
        }

        if (!usesFake) yield break;

        foreach (var r in new[] { "1", "3", "11" })
            yield return seed.With("dpi-desync-repeats", r).Named($"повторов {r}");

        if (seed.Get("dpi-desync-fooling") is not null)
        {
            foreach (var f in Foolings.Where(f => f != seed.Get("dpi-desync-fooling")))
                yield return seed.With("dpi-desync-fooling", f).Named($"fooling {f}");
            yield return seed.With("dpi-desync-fooling", null).With("dpi-desync-autottl", "").Named("autottl вместо fooling");
        }
        else if (seed.Get("dpi-desync-autottl") is not null)
        {
            foreach (var t in new[] { "-1:3-20", "+1:3-20", "2:3-64" })
                yield return seed.With("dpi-desync-autottl", t).Named($"autottl {t}");
            foreach (var t in new[] { "3", "5", "8" })
                yield return seed.With("dpi-desync-autottl", null).With("dpi-desync-ttl", t).Named($"ttl {t}");
        }

        if (seed.Get("dpi-desync-fake-tls") is not null)
        {
            yield return seed.With("dpi-desync-fake-tls", "tls_clienthello_vk_com.bin").Named("фейк vk.com");
            yield return seed.With("dpi-desync-fake-tls", "!").With("dpi-desync-fake-tls-mod", "rnd,dupsid,sni=www.google.com").Named("фейк rnd-sni");
        }
    }

    /// <summary>Стратегия для замера: только tcp/443 и только домены из списка целей.</summary>
    public static Strategy ToProbeStrategy(Candidate c, string targetsListId) => new()
    {
        Id = "search-probe",
        Name = c.Label,
        Intercept = new Intercept { Tcp = "443" },
        Profiles = [new Profile { Hostlists = [targetsListId], Args = ["filter-tcp=443", .. c.Args] }],
    };

    /// <summary>
    /// Итоговая стратегия из найденного кандидата. Тот же профиль ставится и на tcp/80: разрезы по host/midsld
    /// работают и для HTTP. QUIC не проверялся, поэтому UDP не перехватывается и браузер уйдёт на TCP.
    /// </summary>
    public static Strategy ToStrategy(Candidate c, string id, string name, IReadOnlyList<string> hostlists, bool autoHostlist, string description) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Intercept = new Intercept { Tcp = "80,443" },
        Profiles = [new Profile { Hostlists = hostlists, AutoHostlist = autoHostlist, Args = ["filter-tcp=80,443", .. c.Args] }],
    };

    private static Candidate C(string label, params string[] args) => new(label, args);
}
