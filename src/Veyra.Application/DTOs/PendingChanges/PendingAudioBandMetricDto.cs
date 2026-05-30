namespace Veyra.Application.DTOs.PendingChanges;

public sealed record PendingAudioBandMetricDto
{
    public string BandDisplayName { get; init; } = string.Empty;
    public double BaselineEnergyRatio { get; init; }
    public double CurrentEnergyRatio { get; init; }
    public double DeltaRatio { get; init; }
    public double SimilarityRatio { get; init; }
}
