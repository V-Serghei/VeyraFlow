using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Security;
using Veyra.Application.DTOs;

namespace Veyra.Infrastructure.Native.Security;

internal sealed class ArtifactKeyManagementService(
    IConfiguration configuration,
    ArtifactMasterKeyStore masterKeyStore,
    ILogger<ArtifactKeyManagementService> log)
    : IArtifactKeyManagementService
{
    private const int CurrentRingVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, byte[]> _derivedKeyCache = new(StringComparer.OrdinalIgnoreCase);

    private bool _initialized;
    private byte[] _masterKey = [];
    private ArtifactKeyRingDocument _ring = new();

    public bool EncryptionEnabled { get; } = ParseBool(
        configuration["Security:ArtifactEncryption:Enabled"],
        defaultValue: true);

    public void EnsureInitialized()
    {
        if (!EncryptionEnabled || _initialized)
            return;

        _initGate.Wait();
        try
        {
            InitializeCore();
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (!EncryptionEnabled || _initialized)
            return;

        await _initGate.WaitAsync(ct);
        try
        {
            InitializeCore();
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<ArtifactKeyRingStateDto> GetKeyRingAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        if (!EncryptionEnabled)
            return new ArtifactKeyRingStateDto(string.Empty, DateTime.UtcNow, []);

        lock (_stateLock)
        {
            var keys = _ring.Keys
                .OrderByDescending(k => k.CreatedAtUtc)
                .ThenByDescending(k => k.KeyId, StringComparer.Ordinal)
                .Select(Map)
                .ToList();

            return new ArtifactKeyRingStateDto(_ring.ActiveKeyId, _ring.UpdatedAtUtc, keys);
        }
    }

    public async Task<ArtifactKeyRecordDto> RotateActiveKeyAsync(
        string? note = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        if (!EncryptionEnabled)
            throw new InvalidOperationException("Artifact encryption is disabled.");

        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            var active = FindActiveKeyOrThrow();

            if (active.Status == ArtifactKeyStatus.Active)
            {
                active.Status = ArtifactKeyStatus.Retired;
                active.RotatedAtUtc = now;
            }

            var next = CreateActiveRecord(now, note);
            _ring.Keys.Add(next);
            _ring.ActiveKeyId = next.KeyId;
            _ring.UpdatedAtUtc = now;

            PersistRingDocument(_ring);
            return Map(next);
        }
    }

    public async Task<ArtifactKeyRecordDto> RevokeKeyAsync(
        string keyId,
        string? note = null,
        bool createReplacementIfActive = true,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        if (!EncryptionEnabled)
            throw new InvalidOperationException("Artifact encryption is disabled.");

        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("Key id is required.", nameof(keyId));

        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            var target = _ring.Keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId.Trim(), StringComparison.OrdinalIgnoreCase));

            if (target is null)
                throw new InvalidOperationException($"Artifact key {keyId} was not found.");

            if (target.Status == ArtifactKeyStatus.Revoked)
                return Map(target);

            var isActive = string.Equals(_ring.ActiveKeyId, target.KeyId, StringComparison.OrdinalIgnoreCase);
            if (isActive && !createReplacementIfActive)
            {
                throw new InvalidOperationException(
                    "Cannot revoke active key without replacement. Enable createReplacementIfActive.");
            }

            target.Status = ArtifactKeyStatus.Revoked;
            target.RevokedAtUtc = now;
            target.Note = MergeNote(target.Note, note);
            _derivedKeyCache.Remove(target.KeyId);

            if (isActive)
            {
                var replacement = CreateActiveRecord(now, "auto_replacement_after_revoke");
                _ring.Keys.Add(replacement);
                _ring.ActiveKeyId = replacement.KeyId;
            }

            _ring.UpdatedAtUtc = now;
            PersistRingDocument(_ring);
            return Map(target);
        }
    }

    public ArtifactEncryptionKeyMaterial GetActiveKeyMaterial()
    {
        EnsureInitialized();

        if (!EncryptionEnabled)
            throw new InvalidOperationException("Artifact encryption is disabled.");

        lock (_stateLock)
        {
            var active = FindActiveKeyOrThrow();
            var keyBytes = DeriveKeyMaterial(active);
            return new ArtifactEncryptionKeyMaterial(active.KeyId, keyBytes.ToArray());
        }
    }

    public bool TryGetKeyMaterial(string keyId, out ArtifactEncryptionKeyMaterial material)
    {
        material = default!;

        EnsureInitialized();

        if (!EncryptionEnabled || string.IsNullOrWhiteSpace(keyId))
            return false;

        lock (_stateLock)
        {
            var record = _ring.Keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (record is null || record.Status == ArtifactKeyStatus.Revoked)
                return false;

            material = new ArtifactEncryptionKeyMaterial(record.KeyId, DeriveKeyMaterial(record).ToArray());
            return true;
        }
    }

    private void InitializeCore()
    {
        if (_initialized)
            return;

        _masterKey = masterKeyStore.LoadOrCreateMasterKey();
        _ring = LoadOrCreateRingDocument();
        _initialized = true;

        log.LogInformation(
            "Artifact key-management initialized. ActiveKey {ActiveKeyId}. Keys {Count}. MasterProtection {Protection}",
            _ring.ActiveKeyId,
            _ring.Keys.Count,
            masterKeyStore.ProtectionMechanism);
    }

    private ArtifactKeyRingDocument LoadOrCreateRingDocument()
    {
        var path = ResolveRingPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        ArtifactKeyRingDocument ring;

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                ring = JsonSerializer.Deserialize<ArtifactKeyRingDocument>(json, JsonOptions) ?? new ArtifactKeyRingDocument();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to parse artifact key-ring file. Rebuilding default key ring.");
                ring = new ArtifactKeyRingDocument();
            }
        }
        else
        {
            ring = new ArtifactKeyRingDocument();
        }

        var normalized = NormalizeRing(ring);
        PersistRingDocument(normalized);
        return normalized;
    }

    private ArtifactKeyRingDocument NormalizeRing(ArtifactKeyRingDocument ring)
    {
        var now = DateTime.UtcNow;
        var normalized = new ArtifactKeyRingDocument
        {
            Version = CurrentRingVersion,
            UpdatedAtUtc = ring.UpdatedAtUtc == default ? now : ring.UpdatedAtUtc,
            ActiveKeyId = ring.ActiveKeyId ?? string.Empty,
            Keys = []
        };

        foreach (var record in ring.Keys)
        {
            if (string.IsNullOrWhiteSpace(record.KeyId))
                continue;

            if (string.IsNullOrWhiteSpace(record.SaltBase64))
                continue;

            if (!TryParseBase64(record.SaltBase64, out var saltBytes) || saltBytes.Length < 16)
                continue;

            normalized.Keys.Add(new ArtifactKeyRecordDocument
            {
                KeyId = record.KeyId.Trim(),
                Status = ArtifactKeyStatus.Normalize(record.Status),
                CreatedAtUtc = record.CreatedAtUtc == default ? now : record.CreatedAtUtc,
                RotatedAtUtc = record.RotatedAtUtc,
                RevokedAtUtc = record.RevokedAtUtc,
                Note = NormalizeNote(record.Note),
                SaltBase64 = record.SaltBase64.Trim()
            });
        }

        if (normalized.Keys.Count == 0)
        {
            var initial = CreateActiveRecord(now, "initial");
            normalized.Keys.Add(initial);
            normalized.ActiveKeyId = initial.KeyId;
            normalized.UpdatedAtUtc = now;
            return normalized;
        }

        var active = normalized.Keys.FirstOrDefault(k =>
            string.Equals(k.KeyId, normalized.ActiveKeyId, StringComparison.OrdinalIgnoreCase)
            && k.Status == ArtifactKeyStatus.Active);

        if (active is null)
        {
            foreach (var key in normalized.Keys.Where(k => k.Status == ArtifactKeyStatus.Active))
            {
                key.Status = ArtifactKeyStatus.Retired;
                key.RotatedAtUtc ??= now;
            }

            var next = CreateActiveRecord(now, "auto_repair_active_key");
            normalized.Keys.Add(next);
            normalized.ActiveKeyId = next.KeyId;
            normalized.UpdatedAtUtc = now;
        }

        return normalized;
    }

    private ArtifactKeyRecordDocument CreateActiveRecord(DateTime now, string? note)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        return new ArtifactKeyRecordDocument
        {
            KeyId = GenerateKeyId(),
            Status = ArtifactKeyStatus.Active,
            CreatedAtUtc = now,
            RotatedAtUtc = null,
            RevokedAtUtc = null,
            Note = NormalizeNote(note),
            SaltBase64 = Convert.ToBase64String(salt)
        };
    }

    private ArtifactKeyRecordDocument FindActiveKeyOrThrow()
    {
        var active = _ring.Keys.FirstOrDefault(k =>
            string.Equals(k.KeyId, _ring.ActiveKeyId, StringComparison.OrdinalIgnoreCase)
            && k.Status == ArtifactKeyStatus.Active);

        return active
               ?? throw new InvalidOperationException("Artifact key ring has no active key.");
    }

    private byte[] DeriveKeyMaterial(ArtifactKeyRecordDocument record)
    {
        if (_derivedKeyCache.TryGetValue(record.KeyId, out var cached) && cached.Length == 32)
            return cached;

        if (!TryParseBase64(record.SaltBase64, out var saltBytes) || saltBytes.Length < 16)
            throw new InvalidOperationException($"Artifact key {record.KeyId} has invalid derivation salt.");

        var info = Encoding.UTF8.GetBytes($"veyra:artifact:data-key:v1:{record.KeyId}");
        var payload = new byte[info.Length + saltBytes.Length];
        Buffer.BlockCopy(info, 0, payload, 0, info.Length);
        Buffer.BlockCopy(saltBytes, 0, payload, info.Length, saltBytes.Length);

        var derived = HMACSHA256.HashData(_masterKey, payload);
        if (derived.Length != 32)
            throw new InvalidOperationException("Artifact key derivation returned invalid key length.");

        _derivedKeyCache[record.KeyId] = derived;
        return derived;
    }

    private void PersistRingDocument(ArtifactKeyRingDocument ring)
    {
        var path = ResolveRingPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        ring.Version = CurrentRingVersion;
        ring.UpdatedAtUtc = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(ring, JsonOptions);
        File.WriteAllText(path, json);
    }

    private string ResolveRingPath()
    {
        var configured = configuration["Security:ArtifactEncryption:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "keys",
            "artifact_keyring.json");
    }

    private static ArtifactKeyRecordDto Map(ArtifactKeyRecordDocument record)
        => new(
            record.KeyId,
            ArtifactKeyStatus.Normalize(record.Status),
            record.CreatedAtUtc,
            record.RotatedAtUtc,
            record.RevokedAtUtc,
            record.Note);

    private static string GenerateKeyId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool ParseBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return bool.TryParse(value.Trim(), out var parsed) ? parsed : defaultValue;
    }

    private static bool TryParseBase64(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            bytes = Convert.FromBase64String(value.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;

        var trimmed = note.Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }

    private static string? MergeNote(string? existing, string? appended)
    {
        var left = NormalizeNote(existing);
        var right = NormalizeNote(appended);

        if (string.IsNullOrWhiteSpace(right))
            return left;

        if (string.IsNullOrWhiteSpace(left))
            return right;

        var merged = $"{left}; {right}";
        return merged.Length <= 512 ? merged : merged[..512];
    }
}
