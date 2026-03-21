using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Infrastructure.Native.Storage;

namespace Veyra.Infrastructure.Native;

public static partial class DependencyInjection
{
    private static IServiceCollection AddNativeStorageServices(this IServiceCollection services)
    {
        services.AddSingleton<IFileContentStore, RustFileContentStore>();
        return services;
    }
}
