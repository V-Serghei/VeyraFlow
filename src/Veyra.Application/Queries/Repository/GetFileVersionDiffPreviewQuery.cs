using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;

namespace Veyra.Application.Queries.Repository;

public sealed record GetFileVersionDiffPreviewQuery(
    long LeftFileVersionId,
    long RightFileVersionId,
    int MaxLines = 3000)
    : IRequest<OperationResult<PendingFileDiffPreviewDto>>;
