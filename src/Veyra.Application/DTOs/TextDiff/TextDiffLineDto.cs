namespace Veyra.Application.DTOs;

public sealed record TextDiffLineDto(
    string Kind,
    int? LeftLineNumber,
    int? RightLineNumber,
    string Text);
