namespace Veyra.Infrastructure.Data.Preview;

internal sealed record AudioSignalAnalysis(
    AudioChannelAnalysis[] Channels,
    float[] MonoSamples,
    double DurationSeconds,
    int SampleRate,
    int OriginalChannelCount,
    int DisplayChannelCount,
    double PeakAmplitude,
    double RmsAmplitude,
    double? StereoCorrelation);
