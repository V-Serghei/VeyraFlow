using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.PendingChanges;

namespace Veyra.Application.Queries.Repository;

public sealed class GetPendingFileDiffPreviewHandler(
    IRepositorySnapshotRepository snapshots,
    ILogger<GetPendingFileDiffPreviewHandler> log)
    : IRequestHandler<GetPendingFileDiffPreviewQuery, OperationResult<PendingFileDiffPreviewDto>>
{
    public async Task<OperationResult<PendingFileDiffPreviewDto>> Handle(
        GetPendingFileDiffPreviewQuery request,
        CancellationToken ct)
    {
        if (request.RepositoryId <= 0)
            return OperationResult<PendingFileDiffPreviewDto>.Fail("Repository is not selected.");

        if (string.IsNullOrWhiteSpace(request.RelativePath))
            return OperationResult<PendingFileDiffPreviewDto>.Fail("File path is required.");

        try
        {
            var preview = await snapshots.GetPendingFileDiffPreviewAsync(
                request.RepositoryId,
                request.RelativePath,
                request.MaxLines,
                ct);

            return OperationResult<PendingFileDiffPreviewDto>.Ok(preview);
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Failed to build pending file diff preview. RepositoryId {RepositoryId}. Path {Path}",
                request.RepositoryId,
                request.RelativePath);

            return OperationResult<PendingFileDiffPreviewDto>.Fail("Unable to build pending file diff preview.");
        }
    }
}
