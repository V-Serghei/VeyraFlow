namespace Veyra.Infrastructure.Data.Preview;

internal sealed record AudioChannelDiffSeries(
    int ChannelIndex,
    string ChannelDisplayName,
    double BaselinePeakAmplitude,
    double CurrentPeakAmplitude,
    double BaselineRmsAmplitude,
    double CurrentRmsAmplitude,
    double SimilarityRatio,
    double ChangedTimeRatio,
    double[] DiffStrength);
