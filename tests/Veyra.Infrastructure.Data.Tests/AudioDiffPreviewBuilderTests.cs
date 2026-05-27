using System.Text;
using Veyra.Infrastructure.Data.Preview;

namespace Veyra.Infrastructure.Data.Tests;

public sealed class AudioDiffPreviewBuilderTests
{
    [Fact]
    public async Task TryBuildAsync_ReturnsWaveformAndChangeMetrics_ForDifferentSignals()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "veyra-audio-diff-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var baselinePath = Path.Combine(tempRoot, "baseline.wav");
        var currentPath = Path.Combine(tempRoot, "current.wav");

        try
        {
            WritePcmWave(
                baselinePath,
                sampleRate: 22050,
                channels:
                [
                    BuildSignal(22050, segments:
                    [
                        new SignalSegment(0.25f, 0.10f, 0.0f),
                        new SignalSegment(0.45f, 0.42f, 440.0f),
                        new SignalSegment(0.30f, 0.12f, 0.0f)
                    ]),
                    BuildSignal(22050, segments:
                    [
                        new SignalSegment(0.20f, 0.08f, 0.0f),
                        new SignalSegment(0.55f, 0.34f, 220.0f),
                        new SignalSegment(0.25f, 0.12f, 0.0f)
                    ])
                ]);

            WritePcmWave(
                currentPath,
                sampleRate: 22050,
                channels:
                [
                    BuildSignal(22050, segments:
                    [
                        new SignalSegment(0.15f, 0.10f, 0.0f),
                        new SignalSegment(0.30f, 0.86f, 660.0f),
                        new SignalSegment(0.25f, 0.28f, 330.0f),
                        new SignalSegment(0.30f, 0.10f, 0.0f)
                    ]),
                    BuildSignal(22050, segments:
                    [
                        new SignalSegment(0.15f, 0.08f, 0.0f),
                        new SignalSegment(0.30f, 0.58f, 330.0f),
                        new SignalSegment(0.25f, 0.22f, 880.0f),
                        new SignalSegment(0.30f, 0.12f, 0.0f)
                    ])
                ]);

            var result = await AudioDiffPreviewBuilder.TryBuildAsync(baselinePath, currentPath, CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(File.Exists(result!.DifferenceAudioPath));
            Assert.True(File.Exists(result!.WaveformImagePath));
            Assert.True(File.Exists(result.SpectrogramImagePath));
            Assert.True(File.Exists(result.SpectralDeltaImagePath));
            Assert.True(result.BaselineDurationSeconds > 0d);
            Assert.True(result.CurrentDurationSeconds > 0d);
            Assert.True(result.ChangedTimeRatio > 0d);
            Assert.True(result.ChangedSegmentCount > 0);
            Assert.True(result.SignalSimilarityRatio < 0.98d);
            Assert.True(result.SpectralSimilarityRatio < 0.995d);
            Assert.True(result.SpectralDeltaRatio > 0d);
            Assert.True(result.BaselinePeakAmplitude > 0d);
            Assert.True(result.CurrentPeakAmplitude > 0d);
            Assert.NotEmpty(result.ChannelMetrics);
            Assert.True(result.ChannelMetrics.Count >= 2);
            Assert.NotEmpty(result.BandMetrics);
            Assert.Equal(3, result.BandMetrics.Count);
            Assert.NotEmpty(result.ChangedSegments);
            Assert.All(result.ChangedSegments, segment =>
            {
                Assert.True(segment.EndSeconds >= segment.StartSeconds);
                Assert.True(segment.DurationSeconds >= 0d);
            });
        }
        finally
        {
            DeleteDirectoryQuietly(tempRoot);
        }
    }

    [Fact]
    public async Task TryBuildAsync_ReturnsNull_WhenFileIsMissing()
    {
        var result = await AudioDiffPreviewBuilder.TryBuildAsync(
            @"D:\missing\baseline.wav",
            @"D:\missing\current.wav",
            CancellationToken.None);

        Assert.Null(result);
    }

    private static float[] BuildSignal(int sampleRate, SignalSegment[] segments)
    {
        var totalSamples = segments.Sum(segment => (int)Math.Round(sampleRate * segment.DurationRatio));
        if (totalSamples <= 0)
            return [];

        var samples = new float[totalSamples];
        var offset = 0;

        foreach (var segment in segments)
        {
            var segmentSamples = Math.Max(1, (int)Math.Round(sampleRate * segment.DurationRatio));
            for (var i = 0; i < segmentSamples && offset < samples.Length; i++, offset++)
            {
                if (segment.FrequencyHz <= 0.01f || segment.Amplitude <= 0.001f)
                {
                    samples[offset] = 0f;
                    continue;
                }

                var time = offset / (double)sampleRate;
                samples[offset] = (float)(segment.Amplitude * Math.Sin(2d * Math.PI * segment.FrequencyHz * time));
            }
        }

        return samples;
    }

    private static void WritePcmWave(string path, int sampleRate, float[][] channels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const short bitsPerSample = 16;
        var channelCount = (short)Math.Max(1, channels.Length);
        var maxSamples = channels.Length == 0 ? 0 : channels.Max(channel => channel.Length);
        var blockAlign = (short)(channelCount * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataLength = maxSamples * blockAlign;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var sampleIndex = 0; sampleIndex < maxSamples; sampleIndex++)
        {
            for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                var source = channelIndex < channels.Length ? channels[channelIndex] : Array.Empty<float>();
                var sample = sampleIndex < source.Length ? source[sampleIndex] : 0f;
                var pcm = (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
                writer.Write(pcm);
            }
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup for temp test artifacts
        }
    }

    private readonly record struct SignalSegment(float DurationRatio, float Amplitude, float FrequencyHz);
}
