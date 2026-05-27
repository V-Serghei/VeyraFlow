using System;
using System.Diagnostics;
using Veyra.Desktop.Services.Monitoring.Models;

namespace Veyra.Desktop.Services.Monitoring;

public sealed class ProcessResourceMonitorService : IProcessResourceMonitorService
{
    private readonly object _gate = new();
    private TimeSpan? _lastCpuTime;
    private DateTime? _lastCpuSampleUtc;

    public ProcessResourceSnapshotDto Capture()
    {
        lock (_gate)
        {
            var nowUtc = DateTime.UtcNow;

            using var process = Process.GetCurrentProcess();
            process.Refresh();

            double? cpuPercent = null;
            var totalProcessorTime = process.TotalProcessorTime;
            if (_lastCpuTime.HasValue && _lastCpuSampleUtc.HasValue)
            {
                var cpuDeltaMs = (totalProcessorTime - _lastCpuTime.Value).TotalMilliseconds;
                var wallDeltaMs = (nowUtc - _lastCpuSampleUtc.Value).TotalMilliseconds;
                if (wallDeltaMs > 0)
                {
                    cpuPercent = Math.Max(
                        0,
                        Math.Min(
                            1000,
                            cpuDeltaMs / (wallDeltaMs * Math.Max(1, Environment.ProcessorCount)) * 100d));
                }
            }

            _lastCpuTime = totalProcessorTime;
            _lastCpuSampleUtc = nowUtc;

            DateTime? startedAtUtc;
            try
            {
                startedAtUtc = process.StartTime.ToUniversalTime();
            }
            catch
            {
                startedAtUtc = null;
            }

            return new ProcessResourceSnapshotDto(
                nowUtc,
                cpuPercent,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                GC.GetTotalMemory(false),
                process.Threads.Count,
                process.HandleCount,
                startedAtUtc);
        }
    }
}
