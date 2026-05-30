namespace Veyra.Application.DTOs.TextDiff;

public sealed record TextDiffLineDto(
    string Kind,
    int? LeftLineNumber,
    int? RightLineNumber,
    string Text);
