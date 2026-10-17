using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ZapretSmart.Core.Engine;

/// <summary>
/// Причины, по которым WinDivert не открывается, и починка той, что чинится сама. winws при ошибке WinDivertOpen
/// выходит с кодом ошибки Windows, его и разбираем (коды из документации WinDivert).
/// <para>
/// Ловушка с кодом 2: WinDivert.dll регистрирует службу драйвера «WinDivert» с путём к WinDivert64.sys и удаляет
/// её только после удачного запуска. Если файла уже нет (его забрал антивирус, или служба осталась от другой программы
/// или старой папки), служба остаётся и указывает в пустоту. Дальше любой запуск находит её, Windows не находит файл,
/// и ошибка повторяется, даже когда рядом с winws.exe лежит исправный WinDivert64.sys.
/// </para>
/// </summary>
public static class WinDivertDoctor
{
    public const string ServiceName = "WinDivert";
    public const string DriverFile = "WinDivert64.sys";

    /// <summary>Строка winws, после которой код выхода означает ошибку WinDivertOpen.</summary>
    public const string OpenFailedMarker = "windivert: error opening filter";

    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;

    /// <summary>
    /// Путь к файлу драйвера из ImagePath службы: «\??\C:\...», «\SystemRoot\...», «System32\...» или обычный путь.
    /// </summary>
    public static string? DriverPath(string? imagePath, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var p = imagePath.Trim().Trim('"');
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return windowsDir.TrimEnd('\\') + p[11..];
        var rooted = (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\') || p.StartsWith(@"\\", StringComparison.Ordinal);
        return rooted ? p : windowsDir.TrimEnd('\\') + "\\" + p.TrimStart('\\');
    }

    /// <summary>Что делать человеку при коде выхода winws после <see cref="OpenFailedMarker"/>; null, если код не про WinDivert.</summary>
    public static string? Explain(int code, bool driverNextToEngine) => code switch
    {
        2 when !driverNextToEngine =>
            $"Рядом с winws.exe нет {DriverFile}: скорее всего, его удалил или забрал в карантин антивирус. Восстановите файл из карантина и добавьте папку Zapret Smart в исключения антивируса или распакуйте архив заново.",
        2 =>
            $"{DriverFile} на месте, но Windows не смогла его загрузить: обычно файл перехватывает антивирус. Добавьте папку Zapret Smart в исключения антивируса и запустите снова.",
        5 => "Нет прав администратора: запустите Zapret Smart от имени администратора.",
        577 =>
            $"Windows не приняла цифровую подпись {DriverFile}. Файл повреждён или подменён: распакуйте архив заново. Если не помогло, установите обновления Windows.",
        654 =>
            "Загружен драйвер WinDivert другой версии от другой программы (GoodbyeDPI, другая сборка zapret). Закройте её и перезагрузите компьютер.",
        ErrorServiceMarkedForDelete =>
            "Служба драйвера WinDivert помечена на удаление, Windows уберёт её только после перезагрузки. Перезагрузите компьютер.",
        1275 =>
            "Загрузку драйвера WinDivert заблокировал антивирус или другая защитная программа, или Windows работает в виртуальной машине без поддержки драйверов. Добавьте папку Zapret Smart в исключения антивируса.",
        1753 =>
            "Выключена служба BFE («Служба базовой фильтрации»), без неё WinDivert не работает. Включите её в services.msc с типом запуска «Автоматически».",
        _ => null,
    };

    /// <summary>
    /// Удаляет службу драйвера, если её файла нет: тогда она не запустится никогда, а WinDivert создаст её заново
    /// из папки движка. Ответ: строка для журнала или null, если чинить нечего. Ничего не бросает.
    /// Служба, указывающая на свой же пропавший драйвер (ownDriver), удаляется молча: про пропавший файл скажет
    /// <see cref="Explain"/>, а иначе одно и то же писалось бы при каждом запуске.
    /// </summary>
    public static string? RepairStaleService(string? ownDriver = null, string serviceName = ServiceName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? driver;
        try
        {
            driver = DriverPath(ReadImagePath(serviceName), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (driver is null || File.Exists(driver)) return null;

        var shown = Share.ShareLog.Redact(driver);
        var error = RemoveService(serviceName);
        if (error is null && string.Equals(driver, ownDriver, StringComparison.OrdinalIgnoreCase)) return null;
        return error is null
            ? $"Служба драйвера {serviceName} ссылалась на удалённый файл {shown} (осталась от другой программы, старой папки или файл забрал антивирус). Служба удалена, драйвер установится заново из папки Zapret Smart."
            : $"Служба драйвера {serviceName} ссылается на удалённый файл {shown}, и удалить её не удалось: {error}. Выполните в командной строке от администратора: sc delete {serviceName}";
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadImagePath(string serviceName) =>
        Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + serviceName, "ImagePath", null) as string;

    /// <summary>Сколько ждать, пока Windows уберёт помеченную на удаление службу.</summary>
    private static readonly TimeSpan GoneTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// null, если службы больше нет; иначе текст ошибки. DeleteService только помечает службу, а убирает её Windows,
    /// когда закрыты все её дескрипторы. Пока она помечена, WinDivert не может создать её заново и падает с кодом
    /// 1072, поэтому ждём, пока служба действительно исчезнет.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? RemoveService(string serviceName)
    {
        const uint ScManagerConnect = 0x0001;
        const uint Delete = 0x00010000;
        const uint QueryStatus = 0x0004;
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return new System.ComponentModel.Win32Exception().Message;
        try
        {
            var service = OpenServiceW(manager, serviceName, Delete);
            if (service == IntPtr.Zero)
            {
                var open = Marshal.GetLastWin32Error();
                return open == ErrorServiceDoesNotExist ? null : new System.ComponentModel.Win32Exception(open).Message;
            }
            try
            {
                if (!DeleteService(service))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != ErrorServiceMarkedForDelete) return new System.ComponentModel.Win32Exception(err).Message;
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }

            var deadline = DateTime.UtcNow + GoneTimeout;
            while (true)
            {
                var probe = OpenServiceW(manager, serviceName, QueryStatus);
                if (probe == IntPtr.Zero && Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist) return null;
                if (probe != IntPtr.Zero) CloseServiceHandle(probe);
                if (DateTime.UtcNow >= deadline)
                    return "служба помечена на удаление, но её держит другая программа. Закройте окно «Службы» (services.msc) или перезагрузите компьютер";
                Thread.Sleep(100);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
