using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.PendingChanges;

namespace Veyra.Application.Queries.Repository;

public sealed record GetPendingFileDiffPreviewQuery(
    int RepositoryId,
    string RelativePath,
    int MaxLines = 3000)
    : IRequest<OperationResult<PendingFileDiffPreviewDto>>;
