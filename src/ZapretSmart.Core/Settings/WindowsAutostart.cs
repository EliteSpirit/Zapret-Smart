using System.Diagnostics;
using System.Security;
using System.Text;
using System.Xml;
using ZapretSmart.Core.Share;

namespace ZapretSmart.Core.Settings;

/// <summary>
/// Запуск Zapret Smart при входе в Windows через Планировщик заданий. Запись в Run реестра не годится: программа
/// требует прав администратора, а такие программы Windows при входе оттуда молча не запускает. Задача создаётся
/// из XML: у задачи, созданной «schtasks /sc onlogon», включено «не запускать от батареи», и на ноутбуке автозапуск
/// тихо не срабатывал бы.
/// </summary>
public static class WindowsAutostart
{
    public const string TaskName = "Zapret Smart";

    /// <summary>Аргумент, с которым задача запускает программу: окно не показывается, программа сразу в трее.</summary>
    public const string Argument = "--autostart";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Папка Windows-пути по последнему слэшу: Path.GetDirectoryName вне Windows обратных слэшей не понимает.</summary>
    private static string Folder(string exe) => exe.LastIndexOfAny(['\\', '/']) is var i and > 0 ? exe[..i] : "";

    /// <summary>
    /// XML задачи: при входе этого пользователя, с наивысшими правами, без ограничений по батарее и времени. Описание
    /// только латиницей: schtasks может выдать его обратно в UTF-16, и если такой вывод прочитан однобайтовой
    /// кодировкой, байты русских букв превращаются в «&lt;» и «&amp;» и ломают XML.
    /// </summary>
    public static string BuildTaskXml(string exe, string userId)
    {
        string E(string s) => SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Zapret Smart minimized to the tray at Windows logon.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{E(userId)}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{E(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{E(exe)}</Command>
                  <Arguments>{Argument}</Arguments>
                  <WorkingDirectory>{E(Folder(exe))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Программа, которую запускает задача, из вывода «schtasks /query /xml»; null, если XML не разобрался.</summary>
    public static string? ParseCommand(string taskXml)
    {
        var doc = new XmlDocument();
        try
        {
            // Объявление с encoding="UTF-16" мешает разбору уже декодированной строки. Если schtasks выдал UTF-16,
            // а прочитан он однобайтовой кодировкой, между символами стоят нули: их убираем.
            taskXml = taskXml.Replace("\0", "");
            var start = taskXml.IndexOf("<Task", StringComparison.Ordinal);
            if (start < 0) return null;
            doc.LoadXml(taskXml[start..]);
        }
        catch (XmlException)
        {
            return null;
        }
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
        return doc.SelectSingleNode("//t:Exec/t:Command", ns)?.InnerText.Trim('"');
    }

    /// <summary>Программа, которую запускает задача; null, если задачи нет. Не на Windows всегда null.</summary>
    public static async Task<string?> QueryAsync(CancellationToken ct, string taskName = TaskName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var r = await Schtasks(["/query", "/tn", taskName, "/xml"], ct);
        return r.ExitCode == 0 ? ParseCommand(r.Output) : null;
    }

    /// <summary>Создаёт или заменяет задачу. Возвращает текст ошибки или null.</summary>
    public static async Task<string?> EnableAsync(string exe, CancellationToken ct, string taskName = TaskName)
    {
        if (!OperatingSystem.IsWindows()) return "автозапуск через Планировщик заданий есть только в Windows";
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var file = Path.Combine(Path.GetTempPath(), $"zapret-smart-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks ждёт XML задачи в UTF-16, как его сохраняет сам Планировщик.
            await File.WriteAllTextAsync(file, BuildTaskXml(exe, user), new UnicodeEncoding(bigEndian: false, byteOrderMark: true), ct);
            var r = await Schtasks(["/create", "/tn", taskName, "/xml", file, "/f"], ct);
            return r.ExitCode == 0 ? null : "не удалось создать задачу автозапуска: " + FirstLine(r);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Удаляет задачу, если она есть. Возвращает текст ошибки или null.</summary>
    public static async Task<string?> DisableAsync(CancellationToken ct, string taskName = TaskName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (await QueryAsync(ct, taskName) is null) return null;
        var r = await Schtasks(["/delete", "/tn", taskName, "/f"], ct);
        return r.ExitCode == 0 ? null : "не удалось убрать задачу автозапуска: " + FirstLine(r);
    }

    private static Task<ProcessResult> Schtasks(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("schtasks")
        {
            StandardOutputEncoding = ProcessRunner.ConsoleEncoding,
            StandardErrorEncoding = ProcessRunner.ConsoleEncoding,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return ProcessRunner.RunAsync(psi, ToolTimeout, ct);
    }

    private static string FirstLine(ProcessResult r) =>
        (r.StartError ?? r.Errors + r.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
