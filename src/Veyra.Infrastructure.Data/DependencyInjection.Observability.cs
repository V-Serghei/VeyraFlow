using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Observability;
using Veyra.Infrastructure.Data.Observability;

namespace Veyra.Infrastructure.Data;

public static partial class DependencyInjection
{
    private static IServiceCollection AddDataObservabilityServices(this IServiceCollection services)
    {
        services.AddScoped<EfOperationJournalService>();
        services.AddScoped<IOperationJournalService, RuntimeAwareOperationJournalService>();
        return services;
    }
}
