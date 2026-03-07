using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Indexing;
using Veyra.Application.DTOs;
using Veyra.Application.Services;
using Veyra.Infrastructure.Native.Interop;

namespace Veyra.Infrastructure.Native.Diffing;

public sealed class RustTextDiffEngine(
    ManagedTextDiffEngine managed,
    ILogger<RustTextDiffEngine> log) : ITextDiffEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TextDiffComputationDto> BuildDiffAsync(
        string leftFilePath,
        string rightFilePath,
        int maxLines,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
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

            return new TextDiffComputationDto(
                payload.AddedLines,
                payload.RemovedLines,
                payload.IsTruncated,
                lines,
                hunks);
        }
        catch (Exception ex) when (IsNativeUnavailable(ex))
        {
            log.LogDebug(ex, "Native diff entrypoint is unavailable. Falling back to managed diff engine.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Native diff failed. Falling back to managed diff engine.");
        }

        return await managed.BuildDiffAsync(leftFilePath, rightFilePath, maxLines, ct);
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

    private sealed record NativeTextDiffPayload
    {
        [JsonPropertyName("added_lines")]
        public int AddedLines { get; init; }

        [JsonPropertyName("removed_lines")]
        public int RemovedLines { get; init; }

        [JsonPropertyName("is_truncated")]
        public bool IsTruncated { get; init; }

        [JsonPropertyName("lines")]
        public List<NativeTextDiffLine> Lines { get; init; } = [];

        [JsonPropertyName("hunks")]
        public List<NativeTextDiffHunk> Hunks { get; init; } = [];
    }

    private sealed record NativeTextDiffHunk
    {
        [JsonPropertyName("sequence")]
        public int Sequence { get; init; }

        [JsonPropertyName("start_line_sequence")]
        public int StartLineSequence { get; init; }

        [JsonPropertyName("end_line_sequence")]
        public int EndLineSequence { get; init; }

        [JsonPropertyName("old_start_line")]
        public int OldStartLine { get; init; }

        [JsonPropertyName("old_line_count")]
        public int OldLineCount { get; init; }

        [JsonPropertyName("new_start_line")]
        public int NewStartLine { get; init; }

        [JsonPropertyName("new_line_count")]
        public int NewLineCount { get; init; }

        [JsonPropertyName("change_kind")]
        public string? ChangeKind { get; init; }
    }

    private sealed record NativeTextDiffLine
    {
        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "equal";

        [JsonPropertyName("left_line_number")]
        public int? LeftLineNumber { get; init; }

        [JsonPropertyName("right_line_number")]
        public int? RightLineNumber { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}

