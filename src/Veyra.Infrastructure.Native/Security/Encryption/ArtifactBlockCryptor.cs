using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Veyra.Infrastructure.Native.Security;

internal sealed class ArtifactBlockCryptor
{
    private const string Magic = "VYRAENC";
    private const byte Version1 = 1;
    private const byte Version2 = 2;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ArtifactKeyManagementService _keyManagement;
    private readonly byte[]? _legacyKey;

    public ArtifactBlockCryptor(
        IConfiguration configuration,
        ArtifactKeyManagementService keyManagement,
        ILogger<ArtifactBlockCryptor> log)
    {
        _keyManagement = keyManagement;
        IsEncryptionEnabled = _keyManagement.EncryptionEnabled;

        if (IsEncryptionEnabled)
            _keyManagement.EnsureInitialized();

        _legacyKey = TryResolveLegacyKey(configuration);

        if (IsEncryptionEnabled)
        {
            var active = _keyManagement.GetActiveKeyMaterial();
            log.LogInformation(
                "Artifact block encryption enabled. Active key {KeyId}. Legacy key present: {HasLegacy}",
                active.KeyId,
                _legacyKey is not null);
        }
        else
        {
            log.LogInformation("Artifact block encryption is disabled by configuration.");
        }
    }

    public bool IsEncryptionEnabled { get; }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string plaintextHashSha256)
    {
        if (!IsEncryptionEnabled)
            return plaintext.ToArray();

        var key = _keyManagement.GetActiveKeyMaterial();
        var nonce = DeriveNonce(key.KeyBytes, plaintextHashSha256);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key.KeyBytes, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var keyIdBytes = Encoding.UTF8.GetBytes(key.KeyId);
        if (keyIdBytes.Length is <= 0 or > byte.MaxValue)
            throw new InvalidOperationException("Active key id cannot be encoded into artifact envelope.");

        var envelope = new byte[Magic.Length + 1 + 1 + keyIdBytes.Length + NonceSize + ciphertext.Length + TagSize];
        Encoding.ASCII.GetBytes(Magic).CopyTo(envelope, 0);

        var offset = Magic.Length;
        envelope[offset++] = Version2;
        envelope[offset++] = (byte)keyIdBytes.Length;

        keyIdBytes.CopyTo(envelope.AsSpan(offset, keyIdBytes.Length));
        offset += keyIdBytes.Length;

        nonce.CopyTo(envelope.AsSpan(offset, NonceSize));
        offset += NonceSize;

        ciphertext.CopyTo(envelope.AsSpan(offset, ciphertext.Length));
        offset += ciphertext.Length;

        tag.CopyTo(envelope.AsSpan(offset, TagSize));

        return envelope;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> stored)
    {
        if (!LooksEncrypted(stored))
            return stored.ToArray();

        var version = stored[Magic.Length];
        return version switch
        {
            Version1 => DecryptV1Envelope(stored),
            Version2 => DecryptV2Envelope(stored),
            _ => throw new InvalidOperationException($"Unsupported encrypted artifact block version {version}.")
        };
    }

    private byte[] DecryptV1Envelope(ReadOnlySpan<byte> stored)
    {
        var keyBytes = _legacyKey
                       ?? (_keyManagement.EncryptionEnabled
                           ? _keyManagement.GetActiveKeyMaterial().KeyBytes
                           : null)
                       ?? throw new InvalidOperationException(
                           "Cannot decrypt legacy encrypted artifact block because no legacy key or active key is available.");

        var nonceStart = Magic.Length + 1;
        var ciphertextStart = nonceStart + NonceSize;
        var ciphertextLength = stored.Length - ciphertextStart - TagSize;

        if (ciphertextLength < 0)
            throw new InvalidOperationException("Encrypted block envelope v1 is invalid.");

        var nonce = stored.Slice(nonceStart, NonceSize).ToArray();
        var ciphertext = stored.Slice(ciphertextStart, ciphertextLength).ToArray();
        var tag = stored.Slice(stored.Length - TagSize, TagSize).ToArray();

        var plaintext = new byte[ciphertextLength];
        using (var aes = new AesGcm(keyBytes, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }

    private byte[] DecryptV2Envelope(ReadOnlySpan<byte> stored)
    {
        var offset = Magic.Length + 1;
        if (offset >= stored.Length)
            throw new InvalidOperationException("Encrypted block envelope v2 is invalid: missing key id length.");

        var keyIdLength = stored[offset++];
        if (keyIdLength <= 0)
            throw new InvalidOperationException("Encrypted block envelope v2 is invalid: key id length is zero.");

        if (offset + keyIdLength + NonceSize + TagSize > stored.Length)
            throw new InvalidOperationException("Encrypted block envelope v2 is invalid: payload is truncated.");

        var keyId = Encoding.UTF8.GetString(stored.Slice(offset, keyIdLength));
        offset += keyIdLength;

        if (!_keyManagement.TryGetKeyMaterial(keyId, out var key))
            throw new InvalidOperationException($"Artifact key {keyId} is unavailable or revoked.");

        var nonce = stored.Slice(offset, NonceSize).ToArray();
        offset += NonceSize;

        var ciphertextLength = stored.Length - offset - TagSize;
        if (ciphertextLength < 0)
            throw new InvalidOperationException("Encrypted block envelope v2 has invalid ciphertext length.");

        var ciphertext = stored.Slice(offset, ciphertextLength).ToArray();
        var tag = stored.Slice(stored.Length - TagSize, TagSize).ToArray();

        var plaintext = new byte[ciphertextLength];
        using (var aes = new AesGcm(key.KeyBytes, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }

    private static bool LooksEncrypted(ReadOnlySpan<byte> data)
    {
        if (data.Length < Magic.Length + 1 + NonceSize + TagSize)
            return false;

        var magic = Encoding.ASCII.GetBytes(Magic);
        if (!data.Slice(0, magic.Length).SequenceEqual(magic))
            return false;

        var version = data[magic.Length];
        return version is Version1 or Version2;
    }

    private static byte[] DeriveNonce(byte[] key, string plaintextHashSha256)
    {
        var hashPayload = string.IsNullOrWhiteSpace(plaintextHashSha256)
            ? "empty"
            : plaintextHashSha256.Trim().ToLowerInvariant();

        var nonceSeed = HMACSHA256.HashData(
            key,
            Encoding.UTF8.GetBytes($"veyra:block:{hashPayload}"));

        return nonceSeed.AsSpan(0, NonceSize).ToArray();
    }

    private static byte[]? TryResolveLegacyKey(IConfiguration configuration)
    {
        var keyBase64 = configuration["Security:ArtifactEncryption:KeyBase64"];
        if (string.IsNullOrWhiteSpace(keyBase64))
            keyBase64 = Environment.GetEnvironmentVariable("VEYRA_ARTIFACT_KEY_BASE64");

        if (!string.IsNullOrWhiteSpace(keyBase64))
        {
            var direct = TryDecodeKeyMaterial(keyBase64.Trim());
            if (direct is not null)
                return direct;
        }

        var keyPath = ResolveLegacyKeyPath(configuration);
        if (!File.Exists(keyPath))
            return null;

        try
        {
            var fromFile = File.ReadAllText(keyPath).Trim();
            return TryDecodeKeyMaterial(fromFile);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveLegacyKeyPath(IConfiguration configuration)
    {
        var configured = configuration["Security:ArtifactEncryption:KeyPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "keys",
            "artifact_encryption.key");
    }

    private static byte[]? TryDecodeKeyMaterial(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32)
                return bytes;
        }
        catch
        {
        }

        try
        {
            var hex = value.Trim();
            if (hex.Length == 64)
            {
                var bytes = Convert.FromHexString(hex);
                if (bytes.Length == 32)
                    return bytes;
            }
        }
        catch
        {
        }

        return null;
    }
}
