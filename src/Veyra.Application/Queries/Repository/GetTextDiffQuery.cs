using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed record GetTextDiffQuery(
    long LeftFileVersionId,
    long RightFileVersionId,
    int MaxLines = 5000)
    : IRequest<OperationResult<TextDiffResultDto>>;
