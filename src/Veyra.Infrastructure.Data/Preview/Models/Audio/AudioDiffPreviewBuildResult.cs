using System.Collections.Generic;

namespace Veyra.Infrastructure.Data.Preview;

public sealed record AudioDiffPreviewBuildResult(
    string DifferenceAudioPath,
    bool IsDifferenceTempFile,
    string WaveformImagePath,
    bool IsWaveformTempFile,
    string SpectrogramImagePath,
    bool IsSpectrogramTempFile,
    string SpectralDeltaImagePath,
    bool IsSpectralDeltaTempFile,
    double BaselineDurationSeconds,
    double CurrentDurationSeconds,
    int BaselineSampleRate,
    int CurrentSampleRate,
    int BaselineChannels,
    int CurrentChannels,
    double BaselinePeakAmplitude,
    double CurrentPeakAmplitude,
    double BaselineRmsAmplitude,
    double CurrentRmsAmplitude,
    double SignalSimilarityRatio,
    double SpectralSimilarityRatio,
    double SpectralDeltaRatio,
    double ChangedTimeRatio,
    int ChangedSegmentCount,
    bool HasDurationMismatch,
    double? BaselineStereoCorrelation,
    double? CurrentStereoCorrelation,
    IReadOnlyList<AudioChannelDiffMetric> ChannelMetrics,
    IReadOnlyList<AudioBandDiffMetric> BandMetrics,
    IReadOnlyList<AudioChangedSegment> ChangedSegments);
