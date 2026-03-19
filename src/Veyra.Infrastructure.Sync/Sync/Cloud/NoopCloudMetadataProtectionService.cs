using Veyra.Application.Abstractions.Security;

namespace Veyra.Infrastructure.Sync.Sync;

internal sealed class NoopCloudMetadataProtectionService : ICloudMetadataProtectionService
{
    public bool CanProtectMetadata => false;

    public bool IsProtected(string? value) => false;

    public string Protect(string value) => value;

    public string? ProtectNullable(string? value) => value;

    public bool TryUnprotect(string? value, out string? plaintext)
    {
        plaintext = value;
        return true;
    }
}
