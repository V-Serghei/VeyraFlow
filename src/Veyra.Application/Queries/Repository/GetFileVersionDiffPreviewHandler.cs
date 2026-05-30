using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.PendingChanges;

namespace Veyra.Application.Queries.Repository;

public sealed class GetFileVersionDiffPreviewHandler(
    IRepositorySnapshotRepository snapshots,
    ILogger<GetFileVersionDiffPreviewHandler> log)
    : IRequestHandler<GetFileVersionDiffPreviewQuery, OperationResult<PendingFileDiffPreviewDto>>
{
    public async Task<OperationResult<PendingFileDiffPreviewDto>> Handle(
        GetFileVersionDiffPreviewQuery request,
        CancellationToken ct)
    {
        if (request.LeftFileVersionId <= 0 || request.RightFileVersionId <= 0)
            return OperationResult<PendingFileDiffPreviewDto>.Fail("Both versions must be selected.");

        if (request.LeftFileVersionId == request.RightFileVersionId)
            return OperationResult<PendingFileDiffPreviewDto>.Fail("Select two different versions.");

        try
        {
            var preview = await snapshots.GetFileVersionDiffPreviewAsync(
                request.LeftFileVersionId,
                request.RightFileVersionId,
                request.MaxLines,
                ct);

            return OperationResult<PendingFileDiffPreviewDto>.Ok(preview);
        }
        catch (Exception ex)
        {
            log.LogError(
                ex,
                "Failed to build file-version diff preview. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                request.LeftFileVersionId,
                request.RightFileVersionId);

            return OperationResult<PendingFileDiffPreviewDto>.Fail("Unable to build file-version preview.");
        }
    }
}
