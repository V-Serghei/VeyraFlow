using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Security;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs.ArtifactKeys;

namespace Veyra.Application.Commands.Security;

public sealed class RevokeArtifactKeyHandler(
    IArtifactKeyManagementService keyManagement,
    ILogger<RevokeArtifactKeyHandler> log)
    : IRequestHandler<RevokeArtifactKeyCommand, OperationResult<ArtifactKeyRecordDto>>
{
    public async Task<OperationResult<ArtifactKeyRecordDto>> Handle(
        RevokeArtifactKeyCommand request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.KeyId))
            return OperationResult<ArtifactKeyRecordDto>.Fail("Key id is required.");

        try
        {
            var key = await keyManagement.RevokeKeyAsync(
                request.KeyId,
                request.Note,
                request.CreateReplacementIfActive,
                ct);

            return OperationResult<ArtifactKeyRecordDto>.Ok(key);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to revoke artifact encryption key {KeyId}", request.KeyId);
            return OperationResult<ArtifactKeyRecordDto>.Fail(ex.Message);
        }
    }
}
