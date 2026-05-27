using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Veyra.Infrastructure.Native.Security;

internal sealed class ArtifactMasterKeyStore
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ArtifactMasterKeyStore> _log;
    private readonly ISecretProtector _protector;

    public ArtifactMasterKeyStore(
        IConfiguration configuration,
        ILogger<ArtifactMasterKeyStore> log)
    {
        _configuration = configuration;
        _log = log;
        _protector = BuildProtector(log);
    }

    public string ProtectionMechanism => _protector.Mechanism;

    public string GetMasterKeyPath()
    {
        var configured = _configuration["Security:ArtifactEncryption:MasterKeyPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VeyraFlow",
            "keys",
            "artifact_master.protected");
    }

    public byte[] LoadOrCreateMasterKey()
    {
        var path = GetMasterKeyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            var encoded = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                var protectedBytes = Convert.FromBase64String(encoded);
                var plaintext = _protector.Unprotect(protectedBytes);
                if (plaintext.Length != 32)
                    throw new InvalidOperationException($"Artifact master key at {path} has invalid length {plaintext.Length}.");

                return plaintext;
            }
        }

        var generated = new byte[32];
        RandomNumberGenerator.Fill(generated);

        var protectedPayload = _protector.Protect(generated);
        File.WriteAllText(path, Convert.ToBase64String(protectedPayload));

        _log.LogInformation(
            "Artifact master key initialized at {Path}. Protection {Mechanism}",
            path,
            _protector.Mechanism);

        return generated;
    }

    private static ISecretProtector BuildProtector(ILogger log)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiSecretProtector();

        log.LogWarning(
            "OS secure secret storage is not available on this platform. Falling back to plaintext local key protection.");

        return new PlaintextSecretProtector();
    }
}

