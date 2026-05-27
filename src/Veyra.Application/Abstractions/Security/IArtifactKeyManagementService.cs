using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Security;

public interface IArtifactKeyManagementService
{
    Task EnsureInitializedAsync(CancellationToken ct = default);

    Task<ArtifactKeyRingStateDto> GetKeyRingAsync(CancellationToken ct = default);

    Task<ArtifactKeyRecordDto> RotateActiveKeyAsync(
        string? note = null,
        CancellationToken ct = default);

    Task<ArtifactKeyRecordDto> RevokeKeyAsync(
        string keyId,
        string? note = null,
        bool createReplacementIfActive = true,
        CancellationToken ct = default);
}
