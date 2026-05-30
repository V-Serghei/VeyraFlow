using MediatR;
using Microsoft.Extensions.Logging;
using System.Text;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.Common.Files;
using Veyra.Application.Common.Results;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.TextDiff;
using Veyra.Application.Services.Diff;

namespace Veyra.Application.Queries.Repository;

public sealed class GetTextDiffHandler(
    IRepositorySnapshotRepository snapshots,
    IFileContentStore contentStore,
    ITextDiffEngine diffEngine,
    ILogger<GetTextDiffHandler> log)
    : IRequestHandler<GetTextDiffQuery, OperationResult<TextDiffResultDto>>
{
    public async Task<OperationResult<TextDiffResultDto>> Handle(GetTextDiffQuery request, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "diff");
        Directory.CreateDirectory(tempDir);

        var leftTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.left.tmp");
        var rightTemp = Path.Combine(tempDir, $"{Guid.NewGuid():N}.right.tmp");

        try
        {
            var maxLines = Math.Clamp(request.MaxLines, 200, 20_000);

            var left = await snapshots.GetFileVersionRestoreDataAsync(request.LeftFileVersionId, ct);
            var right = await snapshots.GetFileVersionRestoreDataAsync(request.RightFileVersionId, ct);

            if (left is null || right is null)
                return OperationResult<TextDiffResultDto>.Fail("One of the selected versions was not found.");

            if (!left.RelativePath.Equals(right.RelativePath, StringComparison.OrdinalIgnoreCase))
                return OperationResult<TextDiffResultDto>.Fail("Selected versions belong to different files.");

            if (left.IsDeletionMarker || right.IsDeletionMarker)
                return OperationResult<TextDiffResultDto>.Fail("Diff preview is not available for deletion versions.");

            var extension = NormalizeExtension(left.Extension) ?? NormalizeExtension(right.Extension);
            if (!CanBuildTextDiff(extension))
                return OperationResult<TextDiffResultDto>.Fail("File format is not supported for text diff.");

            var cached = await snapshots.GetStoredTextDiffAsync(request.LeftFileVersionId, request.RightFileVersionId, maxLines, ct);
            if (cached is not null)
                return OperationResult<TextDiffResultDto>.Ok(cached);

            if ((left.Blocks.Count == 0 && left.SizeBytes > 0) || (right.Blocks.Count == 0 && right.SizeBytes > 0))
                return OperationResult<TextDiffResultDto>.Fail("Blocks are missing for one of the selected versions.");

            await contentStore.RestoreFileAsync(left.Blocks, leftTemp, true, ct: ct);
            await contentStore.RestoreFileAsync(right.Blocks, rightTemp, true, ct: ct);

            var computed = await BuildDiffAsync(leftTemp, rightTemp, extension, maxLines, ct);

            var result = new TextDiffResultDto(
                left.RelativePath,
                left.FileVersionId,
                right.FileVersionId,
                computed.AddedLines,
                computed.RemovedLines,
                computed.IsTruncated,
                computed.Lines,
                computed.Hunks);

            try
            {
                await snapshots.SaveStoredTextDiffAsync(result, maxLines, ct);
            }
            catch (Exception cacheEx)
            {
                log.LogWarning(
                    cacheEx,
                    "Failed to persist text diff cache. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                    request.LeftFileVersionId,
                    request.RightFileVersionId);
            }

            return OperationResult<TextDiffResultDto>.Ok(result);
        }
        catch (Exception ex)
        {
            var safeError = FormatDiffBuildError(ex);

            log.LogError(
                ex,
                "Failed to build text diff. LeftVersion {LeftVersion}. RightVersion {RightVersion}",
                request.LeftFileVersionId,
                request.RightFileVersionId);

            return OperationResult<TextDiffResultDto>.Fail(safeError);
        }
        finally
        {
            TryDelete(leftTemp);
            TryDelete(rightTemp);
        }
    }

    private async Task<TextDiffComputationDto> BuildDiffAsync(
        string leftFilePath,
        string rightFilePath,
        string? extension,
        int maxLines,
        CancellationToken ct)
    {
        if (!WordSemanticProjection.IsWordOoxmlExtension(extension))
            return await diffEngine.BuildDiffAsync(leftFilePath, rightFilePath, maxLines, ct);

        var semanticTempDir = Path.Combine(Path.GetTempPath(), "VeyraFlow", "word-diff");
        Directory.CreateDirectory(semanticTempDir);

        var leftSemanticTemp = Path.Combine(semanticTempDir, $"{Guid.NewGuid():N}.left.txt");
        var rightSemanticTemp = Path.Combine(semanticTempDir, $"{Guid.NewGuid():N}.right.txt");

        try
        {
            var leftSemanticLines = WordSemanticProjection.ExtractSemanticLines(leftFilePath);
            var rightSemanticLines = WordSemanticProjection.ExtractSemanticLines(rightFilePath);

            await File.WriteAllLinesAsync(leftSemanticTemp, leftSemanticLines, Encoding.UTF8, ct);
            await File.WriteAllLinesAsync(rightSemanticTemp, rightSemanticLines, Encoding.UTF8, ct);

            return await diffEngine.BuildDiffAsync(leftSemanticTemp, rightSemanticTemp, maxLines, ct);
        }
        finally
        {
            TryDelete(leftSemanticTemp);
            TryDelete(rightSemanticTemp);
        }
    }

    private static bool CanBuildTextDiff(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        return KnownFileExtensions.IsTextDiffExtension(extension) || WordSemanticProjection.IsWordOoxmlExtension(extension);
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;

        return normalized;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string FormatDiffBuildError(Exception ex)
    {
        var text = ex.ToString();

        if (text.Contains("Managed fallback cannot restore native block hash", StringComparison.OrdinalIgnoreCase))
        {
            return "Diff is unavailable for this pair right now: selected versions use native block format, but native restore entrypoints are unavailable. Rebuild/update veyra_core and run Reindex data, then retry.";
        }

        if (text.Contains("Block file not found for restore", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Blocks are missing", StringComparison.OrdinalIgnoreCase))
        {
            return "Diff is unavailable because some version blocks are missing. Run Repair data or Reindex data, then retry.";
        }

        if (text.Contains("decryption", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Artifact key", StringComparison.OrdinalIgnoreCase))
        {
            return "Diff is unavailable because encrypted artifact blocks cannot be decrypted with current keys.";
        }

        return ex.Message;
    }
}
