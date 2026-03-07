using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Behaviors;
using Veyra.Application.Services;

namespace Veyra.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddAutoMapper(cfg => cfg.AddMaps(typeof(DependencyInjection).Assembly));
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        services.AddScoped<ManagedTextDiffEngine>();
        services.AddScoped<ITextDiffEngine>(sp => sp.GetRequiredService<ManagedTextDiffEngine>());

        services.AddScoped<ManagedSnapshotComparisonEngine>();
        services.AddScoped<ISnapshotComparisonEngine>(sp => sp.GetRequiredService<ManagedSnapshotComparisonEngine>());

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

        return services;
    }
}
