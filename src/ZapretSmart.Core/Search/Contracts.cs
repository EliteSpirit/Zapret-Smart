using ZapretSmart.Core.Strategies;

namespace ZapretSmart.Core.Search;

public sealed record ProbeResult(string Domain, bool Ok, TimeSpan Elapsed, long Bytes, string? Error);

public interface IProber
{
    Task<ProbeResult> ProbeAsync(string domain, CancellationToken ct);
}

public interface IEngineHost
{
    /// <summary>Запускает движок и дожидается начала перехвата. Движок останавливается при DisposeAsync сессии.</summary>
    Task<IAsyncDisposable> StartAsync(Strategy strategy, CancellationToken ct);
}

public sealed class EngineStartException(string message) : Exception(message);
