using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Security;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Security;

public sealed class RotateArtifactKeyHandler(
    IArtifactKeyManagementService keyManagement,
    ILogger<RotateArtifactKeyHandler> log)
    : IRequestHandler<RotateArtifactKeyCommand, OperationResult<ArtifactKeyRecordDto>>
{
    public async Task<OperationResult<ArtifactKeyRecordDto>> Handle(
        RotateArtifactKeyCommand request,
        CancellationToken ct)
    {
        try
        {
            var key = await keyManagement.RotateActiveKeyAsync(request.Note, ct);
            return OperationResult<ArtifactKeyRecordDto>.Ok(key);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to rotate artifact encryption key.");
            return OperationResult<ArtifactKeyRecordDto>.Fail(ex.Message);
        }
    }
}
