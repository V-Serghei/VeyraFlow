namespace Veyra.Application.DTOs.TextDiff;

public sealed record TextDiffHunkDto(
    int Sequence,
    int StartLineSequence,
    int EndLineSequence,
    int OldStartLine,
    int OldLineCount,
    int NewStartLine,
    int NewLineCount,
    string ChangeKind);
