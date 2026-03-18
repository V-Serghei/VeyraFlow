using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Veyra.Desktop.Services.Execution;

public sealed class ServiceScopeExecutor(IServiceScopeFactory scopeFactory) : IServiceScopeExecutor
{
    public async Task<TResult> ExecuteAsync<TService, TResult>(
        Func<TService, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        ct.ThrowIfCancellationRequested();
        return await operation(service, ct);
    }

    public async Task ExecuteAsync<TService>(
        Func<TService, CancellationToken, Task> operation,
        CancellationToken ct = default)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        ct.ThrowIfCancellationRequested();
        await operation(service, ct);
    }
}
