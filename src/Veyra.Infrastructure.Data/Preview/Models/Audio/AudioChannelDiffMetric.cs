namespace Veyra.Infrastructure.Data.Preview;

public sealed record AudioChannelDiffMetric(
    int ChannelIndex,
    string ChannelDisplayName,
    double BaselinePeakAmplitude,
    double CurrentPeakAmplitude,
    double BaselineRmsAmplitude,
    double CurrentRmsAmplitude,
    double SimilarityRatio,
    double ChangedTimeRatio);
