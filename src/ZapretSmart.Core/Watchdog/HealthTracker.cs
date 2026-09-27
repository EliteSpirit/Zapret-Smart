namespace ZapretSmart.Core.Watchdog;

public enum HealthVerdict
{
    /// <summary>Первая проверка после запуска: запомнили, сколько сайтов открывается.</summary>
    Baseline,
    Healthy,
    /// <summary>Хуже базового уровня, но ещё не столько раз подряд, чтобы действовать.</summary>
    Degraded,
    /// <summary>Пора искать другую стратегию.</summary>
    Switch,
    /// <summary>Хуже базового уровня, но переключались недавно: ждём, чтобы не метаться между стратегиями.</summary>
    Cooldown,
    /// <summary>Проверочные сайты не открывались с самого начала: переключение вслепую не поможет, нужен поиск.</summary>
    NeverWorked,
}

/// <summary>
/// Решает, когда переключать стратегию. Сравнивает с уровнем, который стратегия показала сразу после запуска,
/// а не с «все сайты»: часть проверочных сайтов может быть заблокирована по IP, и стратегия в этом не виновата.
/// </summary>
public sealed class HealthTracker(int failuresBeforeSwitch, TimeSpan switchCooldown)
{
    private int? _baseline;
    private int _drops;
    private DateTime? _lastSwitchUtc;

    public int? Baseline => _baseline;

    public HealthVerdict Observe(int passed, DateTime nowUtc)
    {
        if (_baseline is null)
        {
            _baseline = passed;
            _drops = 0;
            return passed == 0 ? HealthVerdict.NeverWorked : HealthVerdict.Baseline;
        }

        if (_baseline == 0) return HealthVerdict.NeverWorked;

        if (passed >= _baseline)
        {
            _drops = 0;
            return HealthVerdict.Healthy;
        }

        _drops++;
        if (_drops < failuresBeforeSwitch) return HealthVerdict.Degraded;
        if (_lastSwitchUtc is { } last && nowUtc - last < switchCooldown) return HealthVerdict.Cooldown;
        return HealthVerdict.Switch;
    }

    /// <summary>После переключения (или перезапуска той же стратегии) базовый уровень задаёт новая проверка.</summary>
    public void Restarted(int passed, DateTime nowUtc, bool switched)
    {
        _baseline = passed;
        _drops = 0;
        if (switched) _lastSwitchUtc = nowUtc;
    }

    public void Reset()
    {
        _baseline = null;
        _drops = 0;
    }
}
