using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Desktop.Services.Auth.Models;

namespace Veyra.Desktop.Services.Auth;

public sealed class LocalCredentialStore(ILogger<LocalCredentialStore> log) : ILocalCredentialStore
{
    private const int Iterations = 150_000;
    private static readonly byte[] Entropy = "VeyraFlow.LocalCredentialStore.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VeyraFlow",
        "local-credentials.bin");

    public async Task SavePasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrWhiteSpace(password))
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var model = await LoadCoreAsync(ct).ConfigureAwait(false);
            model.Entries.RemoveAll(entry => string.Equals(entry.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));

            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = DeriveHash(password, salt, Iterations);

            model.Entries.Add(new LocalCredentialEntry
            {
                Username = normalizedUsername,
                SaltBase64 = Convert.ToBase64String(salt),
                HashBase64 = Convert.ToBase64String(hash),
                Iterations = Iterations,
                UpdatedAtUtc = DateTime.UtcNow
            });

            await SaveCoreAsync(model, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> VerifyPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrWhiteSpace(password))
            return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var model = await LoadCoreAsync(ct).ConfigureAwait(false);
            var entry = model.Entries.FirstOrDefault(item =>
                string.Equals(item.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return false;

            var salt = Convert.FromBase64String(entry.SaltBase64);
            var expectedHash = Convert.FromBase64String(entry.HashBase64);
            var actualHash = DeriveHash(password, salt, Math.Max(50_000, entry.Iterations));
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to verify local credential for {Username}", normalizedUsername);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasPasswordAsync(string username, CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var model = await LoadCoreAsync(ct).ConfigureAwait(false);
            return model.Entries.Any(entry =>
                string.Equals(entry.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LocalCredentialFileModel> LoadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_storePath))
            return new LocalCredentialFileModel();

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_storePath, ct).ConfigureAwait(false);
            if (protectedBytes.Length == 0)
                return new LocalCredentialFileModel();

            var jsonBytes = Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<LocalCredentialFileModel>(jsonBytes, JsonOptions)
                ?? new LocalCredentialFileModel();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load local credential store. Treating it as empty.");
            return new LocalCredentialFileModel();
        }
    }

    private async Task SaveCoreAsync(LocalCredentialFileModel model, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions);
        var protectedBytes = Protect(jsonBytes);
        await File.WriteAllBytesAsync(_storePath, protectedBytes, ct).ConfigureAwait(false);
    }

    private static byte[] DeriveHash(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static string NormalizeUsername(string username)
        => (username ?? string.Empty).Trim().ToLowerInvariant();

    private static byte[] Protect(byte[] plaintext)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

        return plaintext;
    }

    private static byte[] Unprotect(byte[] protectedPayload)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(protectedPayload, Entropy, DataProtectionScope.CurrentUser);

        return protectedPayload;
    }
}
