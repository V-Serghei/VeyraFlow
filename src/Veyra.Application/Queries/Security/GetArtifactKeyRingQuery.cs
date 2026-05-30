using MediatR;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.ArtifactKeys;

namespace Veyra.Application.Queries.Security;

public sealed record GetArtifactKeyRingQuery : IRequest<ArtifactKeyRingStateDto>;
