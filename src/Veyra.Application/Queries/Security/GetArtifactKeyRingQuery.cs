using MediatR;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Security;

public sealed record GetArtifactKeyRingQuery : IRequest<ArtifactKeyRingStateDto>;
