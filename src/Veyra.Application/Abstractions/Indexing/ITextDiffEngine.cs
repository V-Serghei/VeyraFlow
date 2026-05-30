using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Indexing;

public interface ITextDiffEngine
{
    public Task<TextDiffComputationDto> BuildDiffAsync(
        string leftFilePath,
        string rightFilePath,
        int maxLines,
        CancellationToken ct = default);
}
