using System.Text;
using System.Xml;
using ZapretSmart.Core.Settings;

namespace ZapretSmart.Core.Tests;

/// <summary>Запуск вместе с Windows через Планировщик заданий.</summary>
public sealed class WindowsAutostartTests
{
    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XmlNode? Node(XmlDocument doc, string path)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", Ns);
        return doc.SelectSingleNode(path, ns);
    }

    /// <summary>
    /// Задача с наивысшими правами (иначе программа с requireAdministrator не запустится), при входе этого пользователя,
    /// без ограничений по батарее и времени работы; путь с «&amp;» и пробелами экранирован.
    /// </summary>
    [Fact]
    public void TaskRunsTheAppElevatedAtLogonAndIgnoresBattery()
    {
        const string exe = @"C:\Games & Tools\Zapret Smart\ZapretSmart.exe";
        var xml = WindowsAutostart.BuildTaskXml(exe, @"PC\artem");
        var doc = new XmlDocument();
        doc.LoadXml(xml[xml.IndexOf("<Task", StringComparison.Ordinal)..]);
        Assert.Equal("HighestAvailable", Node(doc, "//t:Principal/t:RunLevel")?.InnerText);
        Assert.Equal(@"PC\artem", Node(doc, "//t:LogonTrigger/t:UserId")?.InnerText);
        Assert.Equal("false", Node(doc, "//t:DisallowStartIfOnBatteries")?.InnerText);
        Assert.Equal("false", Node(doc, "//t:StopIfGoingOnBatteries")?.InnerText);
        Assert.Equal("PT0S", Node(doc, "//t:ExecutionTimeLimit")?.InnerText);
        Assert.Equal(exe, Node(doc, "//t:Exec/t:Command")?.InnerText);
        Assert.Equal(WindowsAutostart.Argument, Node(doc, "//t:Exec/t:Arguments")?.InnerText);
        Assert.Equal(@"C:\Games & Tools\Zapret Smart", Node(doc, "//t:Exec/t:WorkingDirectory")?.InnerText);
        Assert.Equal(exe, WindowsAutostart.ParseCommand(xml));
    }

    [Fact]
    public void CommandIsReadFromSchtasksOutputInAnyEncoding()
    {
        var xml = WindowsAutostart.BuildTaskXml(@"C:\Apps\ZapretSmart.exe", "u");
        Assert.Equal(@"C:\Apps\ZapretSmart.exe", WindowsAutostart.ParseCommand(xml));
        // schtasks выдал UTF-16, а прочитан он однобайтовой кодировкой: между символами нули.
        var utf16AsBytes = Encoding.Latin1.GetString(Encoding.Unicode.GetBytes(xml));
        Assert.Equal(@"C:\Apps\ZapretSmart.exe", WindowsAutostart.ParseCommand(utf16AsBytes));
        Assert.Equal(@"C:\Apps\x.exe", WindowsAutostart.ParseCommand(
            $"<Task xmlns=\"{Ns}\"><Actions><Exec><Command>\"C:\\Apps\\x.exe\"</Command></Exec></Actions></Task>"));
        Assert.Null(WindowsAutostart.ParseCommand("ERROR: The system cannot find the file specified."));
        Assert.Null(WindowsAutostart.ParseCommand("<Task><broken"));
    }

    public sealed class AdminWindowsFactAttribute : FactAttribute
    {
        public AdminWindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ZS_LIVE") != "1")
                Skip = "нужны Windows и права администратора (ZS_LIVE=1): тест создаёт и удаляет задачу Планировщика";
        }
    }

    /// <summary>Задача создаётся, читается, заменяется и удаляется. Имя своё, чтобы не тронуть настоящую задачу.</summary>
    [AdminWindowsFact]
    public async Task TaskIsCreatedReplacedAndRemovedInTheScheduler()
    {
        var name = "Zapret Smart Test " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.Null(await WindowsAutostart.QueryAsync(CancellationToken.None, name));
            Assert.Null(await WindowsAutostart.EnableAsync(@"C:\Program Files\Zapret Smart Test\ZapretSmart.exe", CancellationToken.None, name));
            Assert.Equal(@"C:\Program Files\Zapret Smart Test\ZapretSmart.exe", await WindowsAutostart.QueryAsync(CancellationToken.None, name));
            Assert.Null(await WindowsAutostart.EnableAsync(@"D:\Moved\ZapretSmart.exe", CancellationToken.None, name));
            Assert.Equal(@"D:\Moved\ZapretSmart.exe", await WindowsAutostart.QueryAsync(CancellationToken.None, name));
        }
        finally
        {
            Assert.Null(await WindowsAutostart.DisableAsync(CancellationToken.None, name));
        }
        Assert.Null(await WindowsAutostart.QueryAsync(CancellationToken.None, name));
        Assert.Null(await WindowsAutostart.DisableAsync(CancellationToken.None, name));
    }
}
