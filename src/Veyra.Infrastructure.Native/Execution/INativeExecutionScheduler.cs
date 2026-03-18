namespace Veyra.Infrastructure.Native.Execution;

public interface INativeExecutionScheduler
{
    Task<T> RunAsync<T>(Func<T> work, CancellationToken ct = default);
    Task RunAsync(Action work, CancellationToken ct = default);
}
