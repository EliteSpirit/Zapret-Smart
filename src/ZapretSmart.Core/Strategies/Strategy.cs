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
    /// <summary>Имена списков доменов. В стратегию попадают только имена, не содержимое.</summary>
    public IReadOnlyList<string> Hostlists { get; init; } = [];

    /// <summary>Имена списков IP. Профиль с ними срабатывает только для адресов из списков.</summary>
    public IReadOnlyList<string> Ipsets { get; init; } = [];

    /// <summary>Движок сам дописывает в автосписок домены, похожие на заблокированные.</summary>
    public bool AutoHostlist { get; init; }

    /// <summary>Опции движка в порядке применения: "name=value" или "name" для флагов. Порядок важен.</summary>
    public required IReadOnlyList<string> Args { get; init; }
}
