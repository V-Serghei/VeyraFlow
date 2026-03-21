using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Indexing;

public interface ITextDiffEngine
{
    Task<TextDiffComputationDto> BuildDiffAsync(
        string leftFilePath,
        string rightFilePath,
        int maxLines,
        CancellationToken ct = default);
}
