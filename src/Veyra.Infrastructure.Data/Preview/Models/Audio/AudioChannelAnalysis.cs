namespace Veyra.Infrastructure.Data.Preview;

internal sealed record AudioChannelAnalysis(
    int ChannelIndex,
    string ChannelDisplayName,
    float[] Peaks,
    float[] Rms,
    double PeakAmplitude,
    double RmsAmplitude);
