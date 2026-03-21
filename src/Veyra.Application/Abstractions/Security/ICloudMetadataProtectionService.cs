namespace Veyra.Application.Abstractions.Security;

public interface ICloudMetadataProtectionService
{
    bool CanProtectMetadata { get; }
    bool IsProtected(string? value);
    string Protect(string value);
    string? ProtectNullable(string? value);
    bool TryUnprotect(string? value, out string? plaintext);
}
