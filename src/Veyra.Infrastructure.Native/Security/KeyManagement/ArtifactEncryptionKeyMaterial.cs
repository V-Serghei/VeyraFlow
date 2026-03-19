namespace Veyra.Infrastructure.Native.Security;

internal sealed record ArtifactEncryptionKeyMaterial(string KeyId, byte[] KeyBytes);
