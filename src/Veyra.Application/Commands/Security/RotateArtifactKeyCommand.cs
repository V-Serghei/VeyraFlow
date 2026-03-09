using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Commands.Security;

public sealed record RotateArtifactKeyCommand(string? Note = null)
    : IRequest<OperationResult<ArtifactKeyRecordDto>>;
