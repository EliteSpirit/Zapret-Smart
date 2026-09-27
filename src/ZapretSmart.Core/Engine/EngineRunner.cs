using System.Diagnostics;

namespace ZapretSmart.Core.Engine;

public sealed class EngineRunner(string executablePath) : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;

    /// <summary>Одно задание на всё приложение: дескриптор живёт до конца процесса и закрывается ОС при его смерти.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static readonly Lazy<KillOnCloseJob> SharedJob = new(() => new KillOnCloseJob());

    public event Action<string>? Output;

    /// <summary>Только stderr, дополнительно к Output. Туда движок пишет причину отказа, а stdout он сбрасывает при выходе, и та строка тонет в хвосте.</summary>
    public event Action<string>? ErrorOutput;
    public event Action<int>? Exited;

    public string ExecutablePath { get; } = executablePath;

    public bool IsRunning
    {
        get { lock (_gate) return _process is { HasExited: false }; }
    }

    public void Start(IReadOnlyList<string> argv)
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException("Движок уже запущен");
            if (!File.Exists(ExecutablePath))
                throw new FileNotFoundException("Не найден исполняемый файл движка", ExecutablePath);

            var psi = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
            };
            foreach (var a in argv) psi.ArgumentList.Add(a);

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                ErrorOutput?.Invoke(e.Data);
                Output?.Invoke(e.Data);
            };
            p.Exited += (_, _) =>
            {
                int code;
                try
                {
                    // Exited приходит раньше, чем дочитан перенаправленный вывод. WaitForExit() без таймаута
                    // ждёт конца потоков: иначе последняя строка движка (причина падения) терялась бы.
                    p.WaitForExit();
                    code = p.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    // Dispose уже освободил процесс; исключение в пуле потоков уронило бы приложение.
                    return;
                }
                Exited?.Invoke(code);
            };

            p.Start();
            BindToAppLifetime(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
        }
    }

    private void BindToAppLifetime(Process p)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            SharedJob.Value.Assign(p);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // Движок работает и без этого, но при падении приложения останется в фоне: предупреждаем, а не падаем.
            Output?.Invoke("! Движок не привязан к приложению (" + e.Message + "): при аварийном закрытии его придётся завершить вручную");
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Process? p;
        lock (_gate) p = _process;
        if (p is null || p.HasExited) return;

        // WinDivert-хэндл закрывается вместе с процессом, поэтому жёсткое завершение безопасно для сети.
        p.Kill(entireProcessTree: true);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.Dispose();
            _process = null;
        }
    }
}
