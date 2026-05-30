using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.Services;
using Veyra.Infrastructure.Native.Execution;
using Veyra.Infrastructure.Native.Interop;
using Veyra.Infrastructure.Native.Runtime;

namespace Veyra.Infrastructure.Native.Diffing;

public sealed class RustTextDiffEngine : ITextDiffEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ManagedTextDiffEngine _managed;
    private readonly INativeExecutionScheduler _scheduler;
    private readonly ILogger<RustTextDiffEngine> _log;
    private readonly object _gate = new();

    private bool _nativeDiffAvailable;

    public RustTextDiffEngine(
        ManagedTextDiffEngine managed,
        INativeExecutionScheduler scheduler,
        ILogger<RustTextDiffEngine> log)
    {
        _managed = managed;
        _scheduler = scheduler;
        _log = log;
        _nativeDiffAvailable = NativeRuntimeHealth.Probe().SupportsTextDiff;
    }

    public async Task<TextDiffComputationDto> BuildDiffAsync(
        string leftFilePath,
        string rightFilePath,
        int maxLines,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_nativeDiffAvailable)
            return await _managed.BuildDiffAsync(leftFilePath, rightFilePath, maxLines, ct);

        try
        {
            return await _scheduler.RunAsync(() =>
            {
                var json = VeyraCoreNative.BuildTextDiffJson(leftFilePath, rightFilePath, maxLines);
                var payload = JsonSerializer.Deserialize<NativeTextDiffPayload>(json, JsonOptions)
                              ?? throw new InvalidOperationException("Native text diff payload is empty.");

                var lines = payload.Lines
                    .Select(l => new TextDiffLineDto(
                        l.Kind,
                        l.LeftLineNumber,
                        l.RightLineNumber,
                        l.Text ?? string.Empty))
                    .ToList();

                var hunks = payload.Hunks
                    .Select(h => new TextDiffHunkDto(
                        h.Sequence,
                        h.StartLineSequence,
                        h.EndLineSequence,
                        h.OldStartLine,
                        h.OldLineCount,
                        h.NewStartLine,
                        h.NewLineCount,
                        h.ChangeKind ?? "modified"))
                    .ToList();

                var result = new TextDiffComputationDto(
                    payload.AddedLines,
                    payload.RemovedLines,
                    payload.IsTruncated,
                    lines,
                    hunks);
                NativeFeatureUsageTracker.MarkNativeHit(NativeFeatureUsageTracker.TextDiff);
                return result;
            }, ct);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.TextDiff);
            DisableNativeDiff(ex);
        }
        catch (Exception ex)
        {
            NativeFeatureUsageTracker.MarkManagedFallback(NativeFeatureUsageTracker.TextDiff);
            _log.LogWarning(ex, "Native diff failed. Falling back to managed diff engine.");
        }

        return await _managed.BuildDiffAsync(leftFilePath, rightFilePath, maxLines, ct);
    }

    private void DisableNativeDiff(Exception ex)
    {
        var switched = false;

        lock (_gate)
        {
            if (_nativeDiffAvailable)
            {
                _nativeDiffAvailable = false;
                switched = true;
            }
        }

        if (switched)
        {
            _log.LogWarning(ex, "Native diff entrypoint became unavailable. Switching to managed diff engine.");
        }
        else
        {
            _log.LogDebug(ex, "Native diff unavailable. Managed diff engine remains active.");
        }
    }

    private static bool IsNativeUnavailable(Exception ex)
    {
        if (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
            return true;

        if (ex is InvalidOperationException ioe)
        {
            return ioe.Message.Contains("entry point", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase)
                   || ioe.Message.Contains("Native text diff", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

}
