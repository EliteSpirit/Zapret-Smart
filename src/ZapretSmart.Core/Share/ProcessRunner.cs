using System.ComponentModel;
using System.Diagnostics;

namespace ZapretSmart.Core.Share;

/// <summary>Итог процесса. TimedOut: не уложился и убит. StartError: не запустился вовсе (нет файла, запрет политики).</summary>
public sealed record ProcessResult(int ExitCode, string Output, string Errors, bool TimedOut, string? StartError);

/// <summary>Запуск служебных программ Windows (netsh, powershell) так, чтобы они не могли подвесить или уронить приложение.</summary>
public static class ProcessRunner
{
    /// <summary>Сколько ждать, пока закроются потоки убитого процесса.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Запускает процесс и ждёт не дольше timeout. stdout и stderr читаются одновременно: непрочитанный поток при
    /// переполнении буфера остановил бы процесс навсегда. Исключения запуска становятся StartError. Зависший процесс
    /// убивается вместе с дочерними. Бросает только OperationCanceledException по ct, и тогда процесс тоже убит.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        Process? started;
        try
        {
            started = Process.Start(psi);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new ProcessResult(-1, "", "", false, $"{Path.GetFileName(psi.FileName)} не запустился: {e.Message}");
        }
        if (started is null) return new ProcessResult(-1, "", "", false, $"{Path.GetFileName(psi.FileName)} не запустился");

        using var process = started;
        // Потоки читаются без таймаута: они закрываются, когда процесс завершился или убит.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var timedOut = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                ct.ThrowIfCancellationRequested();
                timedOut = true;
            }
        }

        var streams = Task.WhenAll(output, errors);
        var drained = await Task.WhenAny(streams, Task.Delay(DrainTimeout, CancellationToken.None)) == streams && streams.IsCompletedSuccessfully;
        return new ProcessResult(
            timedOut ? -1 : process.ExitCode,
            drained ? output.Result : "",
            drained ? errors.Result : "",
            timedOut,
            null);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Уже завершился или убить нельзя: ждать его всё равно больше не будем.
        }
    }
}
