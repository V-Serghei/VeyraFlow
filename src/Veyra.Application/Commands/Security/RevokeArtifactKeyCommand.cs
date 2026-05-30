using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs.ArtifactKeys;

namespace Veyra.Application.Commands.Security;

public sealed record RevokeArtifactKeyCommand(
    string KeyId,
    string? Note = null,
    bool CreateReplacementIfActive = true)
    : IRequest<OperationResult<ArtifactKeyRecordDto>>;
