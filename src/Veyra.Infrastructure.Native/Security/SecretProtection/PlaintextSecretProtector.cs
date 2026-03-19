namespace Veyra.Infrastructure.Native.Security;

internal sealed class PlaintextSecretProtector : ISecretProtector
{
    public string Mechanism => "plaintext_fallback";

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
        => plaintext.ToArray();

    public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload)
        => protectedPayload.ToArray();
}
