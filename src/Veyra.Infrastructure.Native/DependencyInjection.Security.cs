using Microsoft.Extensions.DependencyInjection;
using Veyra.Application.Abstractions.Security;
using Veyra.Infrastructure.Native.Security;

namespace Veyra.Infrastructure.Native;

public static partial class DependencyInjection
{
    private static IServiceCollection AddNativeSecurityServices(this IServiceCollection services)
    {
        services.AddSingleton<ArtifactMasterKeyStore>();
        services.AddSingleton<ArtifactKeyManagementService>();
        services.AddSingleton<IArtifactKeyManagementService>(sp => sp.GetRequiredService<ArtifactKeyManagementService>());
        services.AddSingleton<ArtifactBlockCryptor>();
        services.AddSingleton<ICloudMetadataProtectionService, CloudMetadataProtectionService>();
        return services;
    }
}
