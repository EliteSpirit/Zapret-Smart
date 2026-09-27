using System.Collections.Concurrent;
using ZapretSmart.Core.Engine;
using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Search;

/// <summary>Dispose синхронно убивает все запущенные хостом движки: при выходе из приложения асинхронная отмена не успевает.</summary>
public sealed class WinwsEngineHost(string executablePath, EngineLayout layout, TimeSpan readyTimeout) : IEngineHost, IDisposable
{
    public const string ReadyMarker = "capture is started";

    private readonly ConcurrentDictionary<EngineRunner, byte> _live = new();

    public async Task<IAsyncDisposable> StartAsync(Strategy strategy, CancellationToken ct)
    {
        var argv = EngineCommandBuilder.Build(strategy, layout).Argv;
        var runner = new EngineRunner(executablePath);
        var output = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        runner.Output += line =>
        {
            output.Enqueue(line);
            if (line.Contains(ReadyMarker, StringComparison.Ordinal)) ready.TrySetResult();
        };
        runner.Exited += code => ready.TrySetException(
            new EngineStartException($"движок завершился с кодом {code}: {string.Join(" | ", output.TakeLast(5))}"));

        _live[runner] = 0;
        try
        {
            runner.Start(argv);
            await ready.Task.WaitAsync(readyTimeout, ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // Антивирус заблокировал запуск, файл занят или нет прав.
            Release(runner);
            throw new EngineStartException("не удалось запустить движок: " + e.Message);
        }
        catch (TimeoutException)
        {
            Release(runner);
            throw new EngineStartException($"движок не начал перехват за {readyTimeout.TotalSeconds:0} с: {string.Join(" | ", output.TakeLast(5))}");
        }
        catch
        {
            Release(runner);
            throw;
        }
        return new Session(this, runner);
    }

    private void Release(EngineRunner runner)
    {
        _live.TryRemove(runner, out _);
        runner.Dispose();
    }

    public void Dispose()
    {
        foreach (var runner in _live.Keys) Release(runner);
    }

    private sealed class Session(WinwsEngineHost host, EngineRunner runner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await runner.StopAsync().ConfigureAwait(false);
            host.Release(runner);
        }
    }
}
