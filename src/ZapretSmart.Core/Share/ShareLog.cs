namespace ZapretSmart.Core.Share;

/// <summary>
/// Журнал раздачи в файле share.log: что просили устройства и чем кончилось, со временем. Запросы устройств пишутся
/// с collapseRepeats, чтобы телефон, который долбится в недоступный сервер, не забил журнал. Файл больше
/// 1 МБ переименовывается в share.old.log. Ошибки записи глотаются: из-за журнала раздача падать не должна.
/// </summary>
public sealed class ShareLog
{
    public const long MaxBytes = 1 << 20;
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(1);
    private const int MaxRemembered = 1000;

    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _lastWritten = [];
    private readonly object _lock = new();

    public ShareLog(string filePath, TimeProvider? time = null)
    {
        FilePath = filePath;
        _time = time ?? TimeProvider.System;
    }

    public string FilePath { get; }

    public string OldFilePath => Path.ChangeExtension(FilePath, ".old.log");

    /// <summary>
    /// Пишет строку. С collapseRepeats такая же строка, записанная меньше минуты назад, пропускается, и ответ false.
    /// </summary>
    public bool Write(string line, bool collapseRepeats = false)
    {
        lock (_lock)
        {
            var now = _time.GetLocalNow();
            if (collapseRepeats)
            {
                if (_lastWritten.TryGetValue(line, out var last) && now - last < RepeatInterval) return false;
                if (_lastWritten.Count >= MaxRemembered) _lastWritten.Clear();
                _lastWritten[line] = now;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) File.Move(FilePath, OldFilePath, overwrite: true);
                File.AppendAllText(FilePath, $"{now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            return true;
        }
    }
}
