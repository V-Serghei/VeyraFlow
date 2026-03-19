namespace Veyra.Infrastructure.Native.Security;

internal interface ISecretProtector
{
    string Mechanism { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedPayload);
}
