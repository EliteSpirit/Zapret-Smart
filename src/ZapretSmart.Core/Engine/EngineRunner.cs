using System.Diagnostics;

namespace ZapretSmart.Core.Engine;

public sealed class EngineRunner(string executablePath) : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;

    public event Action<string>? Output;
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
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); };
            p.Exited += (_, _) => Exited?.Invoke(p.ExitCode);

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
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
