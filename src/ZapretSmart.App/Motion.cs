using System.Runtime.InteropServices;

namespace ZapretSmart.App;

/// <summary>
/// «Анимация элементов управления и окон» в настройках Windows (Специальные возможности → Эффекты анимации).
/// Выключена — убираем движение, оставляем только плавную смену цвета и прозрачности.
/// </summary>
public static class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool IsReduced { get; } = Detect();

    private static bool Detect()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var enabled = true;
        return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) && !enabled;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint winIni);
}
