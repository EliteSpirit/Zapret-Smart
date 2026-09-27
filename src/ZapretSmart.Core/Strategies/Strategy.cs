namespace ZapretSmart.Core.Strategies;

public sealed record Strategy
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required Intercept Intercept { get; init; }
    public required IReadOnlyList<Profile> Profiles { get; init; }
}

/// <summary>Порты, которые WinDivert перехватывает у ОС (--wf-tcp / --wf-udp).</summary>
public sealed record Intercept
{
    public string? Tcp { get; init; }
    public string? Udp { get; init; }
}

public sealed record Profile
{
    /// <summary>Идентификатор локального списка доменов. В стратегию попадает только id, не содержимое.</summary>
    public string? Hostlist { get; init; }

    /// <summary>Опции движка в порядке применения: "name=value" или "name" для флагов. Порядок важен.</summary>
    public required IReadOnlyList<string> Args { get; init; }
}
