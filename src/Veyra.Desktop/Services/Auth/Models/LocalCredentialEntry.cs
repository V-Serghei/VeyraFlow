using System;

namespace Veyra.Desktop.Services.Auth.Models;

internal sealed class LocalCredentialEntry
{
    public required string Username { get; init; }
    public required string SaltBase64 { get; init; }
    public required string HashBase64 { get; init; }
    public required int Iterations { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
}
