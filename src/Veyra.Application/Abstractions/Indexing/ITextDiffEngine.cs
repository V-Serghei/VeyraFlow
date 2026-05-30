using Veyra.Application.DTOs;
using Veyra.Application.DTOs.TextDiff;

namespace Veyra.Application.Abstractions.Indexing;

public interface ITextDiffEngine
{
    public Task<TextDiffComputationDto> BuildDiffAsync(
        string leftFilePath,
        string rightFilePath,
        int maxLines,
        CancellationToken ct = default);
}
