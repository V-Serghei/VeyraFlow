using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Application.DTOs;

namespace Veyra.Desktop.Services.Repositories;

public interface IRepositoryRelocationDetector
{
    Task<RepositoryRelocationSuggestion> SuggestAsync(
        string previousPath,
        IReadOnlyList<RepositoryScanEntryDto> latestEntries,
        CancellationToken ct = default);
}

public sealed record RepositoryRelocationSuggestion(
    string? SuggestedPath,
    int SampleCount,
    int MatchedSampleCount,
    double Confidence,
    bool IsAmbiguous)
{
    public bool HasSuggestion => !string.IsNullOrWhiteSpace(SuggestedPath);
    public bool CanAutoRelink => HasSuggestion && !IsAmbiguous;

    public static RepositoryRelocationSuggestion None { get; } = new(null, 0, 0, 0, false);
}
