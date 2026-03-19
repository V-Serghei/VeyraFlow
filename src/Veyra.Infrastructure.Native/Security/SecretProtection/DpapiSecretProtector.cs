using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace Veyra.Infrastructure.Native.Security;

[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy =
        "VeyraFlow.Artifact.MasterKey.v1"u8.ToArray();

    public string Mechanism => "dpapi_current_user";

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
        => ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload)
        => ProtectedData.Unprotect(protectedPayload.ToArray(), Entropy, DataProtectionScope.CurrentUser);
}
