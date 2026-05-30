using MediatR;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.FileVersions;

namespace Veyra.Application.Queries.Repository;

public sealed record GetFileVersionTextContentQuery(
    long FileVersionId,
    int MaxBytes = 2_000_000)
    : IRequest<OperationResult<FileVersionTextContentDto>>;
