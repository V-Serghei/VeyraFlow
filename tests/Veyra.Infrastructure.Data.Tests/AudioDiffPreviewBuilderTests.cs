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
            WriteMonoPcmWave(
                baselinePath,
                sampleRate: 22050,
                samples: BuildSignal(22050, segments:
                [
                    new SignalSegment(0.45f, 0.35f, 0.0f),
                    new SignalSegment(0.45f, 0.35f, 440.0f),
                    new SignalSegment(0.10f, 0.10f, 0.0f)
                ]));

            WriteMonoPcmWave(
                currentPath,
                sampleRate: 22050,
                samples: BuildSignal(22050, segments:
                [
                    new SignalSegment(0.20f, 0.35f, 0.0f),
                    new SignalSegment(0.35f, 0.80f, 660.0f),
                    new SignalSegment(0.25f, 0.18f, 330.0f),
                    new SignalSegment(0.20f, 0.10f, 0.0f)
                ]));

            var result = await AudioDiffPreviewBuilder.TryBuildAsync(baselinePath, currentPath, CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(File.Exists(result!.WaveformImagePath));
            Assert.True(result.BaselineDurationSeconds > 0d);
            Assert.True(result.CurrentDurationSeconds > 0d);
            Assert.True(result.ChangedTimeRatio > 0d);
            Assert.True(result.ChangedSegmentCount > 0);
            Assert.True(result.SignalSimilarityRatio < 0.98d);
            Assert.True(result.BaselinePeakAmplitude > 0d);
            Assert.True(result.CurrentPeakAmplitude > 0d);
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

    private static void WriteMonoPcmWave(string path, int sampleRate, float[] samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        const short bitsPerSample = 16;
        const short channels = 1;
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataLength = samples.Length * blockAlign;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        foreach (var sample in samples)
        {
            var pcm = (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
            writer.Write(pcm);
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
