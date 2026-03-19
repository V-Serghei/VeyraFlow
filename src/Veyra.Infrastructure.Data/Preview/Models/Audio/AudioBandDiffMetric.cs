namespace Veyra.Infrastructure.Data.Preview;

public sealed record AudioBandDiffMetric(
    string BandDisplayName,
    double BaselineEnergyRatio,
    double CurrentEnergyRatio,
    double DeltaRatio,
    double SimilarityRatio);
