using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Security;

namespace Veyra.Infrastructure.Native.Security;

internal sealed class CloudMetadataProtectionService(
    ArtifactMasterKeyStore masterKeyStore,
    ILogger<CloudMetadataProtectionService> log)
    : ICloudMetadataProtectionService
{
    private const string Prefix = "vyrm1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("veyra:cloud-metadata:v1");

    private readonly byte[] _key = masterKeyStore.LoadOrCreateMasterKey();

    public bool CanProtectMetadata => true;

    public bool IsProtected(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
        }

        var payload = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(payload, 0);
        ciphertext.CopyTo(payload, NonceSize);
        tag.CopyTo(payload, NonceSize + ciphertext.Length);

        return Prefix + EncodeBase64Url(payload);
    }

    public string? ProtectNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? value : Protect(value);

    public bool TryUnprotect(string? value, out string? plaintext)
    {
        plaintext = value;
        if (string.IsNullOrWhiteSpace(value) || !IsProtected(value))
            return true;

        try
        {
            var payload = DecodeBase64Url(value[Prefix.Length..]);
            if (payload.Length < NonceSize + TagSize)
            {
                plaintext = null;
                return false;
            }

            var ciphertextLength = payload.Length - NonceSize - TagSize;
            if (ciphertextLength < 0)
            {
                plaintext = null;
                return false;
            }

            var nonce = payload.AsSpan(0, NonceSize).ToArray();
            var ciphertext = payload.AsSpan(NonceSize, ciphertextLength).ToArray();
            var tag = payload.AsSpan(NonceSize + ciphertextLength, TagSize).ToArray();
            var buffer = new byte[ciphertextLength];

            using (var aes = new AesGcm(_key, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, buffer, AssociatedData);
            }

            plaintext = Encoding.UTF8.GetString(buffer);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to decrypt protected cloud metadata.");
            plaintext = null;
            return false;
        }
    }

    private static string EncodeBase64Url(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value
            .Replace('-', '+')
            .Replace('_', '/');

        var remainder = normalized.Length % 4;
        if (remainder > 0)
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');

        return Convert.FromBase64String(normalized);
    }
}
