using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Services;
using Veyra.Infrastructure.Native.Diffing;
using Veyra.Infrastructure.Native.Execution;

namespace Veyra.Infrastructure.Native;

public static partial class DependencyInjection
{
    private static IServiceCollection AddNativeDiffingServices(this IServiceCollection services)
    {
        services.AddScoped<ITextDiffEngine>(sp =>
            new RustTextDiffEngine(
                sp.GetRequiredService<ManagedTextDiffEngine>(),
                sp.GetRequiredService<INativeExecutionScheduler>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RustTextDiffEngine>>()));

        services.AddScoped<ISnapshotComparisonEngine>(sp =>
            new RustSnapshotComparisonEngine(
                sp.GetRequiredService<ManagedSnapshotComparisonEngine>(),
                sp.GetRequiredService<INativeExecutionScheduler>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RustSnapshotComparisonEngine>>()));

        return services;
    }
}
