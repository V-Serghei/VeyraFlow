namespace Veyra.Desktop.Services.Preview;

public sealed record NativeWordCompareOptions(
    bool CompareFormatting,
    bool CompareCaseChanges,
    bool CompareWhitespace,
    bool CompareTables,
    bool CompareHeaders,
    bool CompareFootnotes,
    bool CompareTextboxes,
    bool CompareFields,
    bool CompareComments,
    bool CompareMoves,
    bool IgnoreAllComparisonWarnings = true,
    string? RevisedAuthor = null)
{
    public static NativeWordCompareOptions ContentOnly { get; } = new(
        CompareFormatting: false,
        CompareCaseChanges: false,
        CompareWhitespace: false,
        CompareTables: true,
        CompareHeaders: true,
        CompareFootnotes: true,
        CompareTextboxes: true,
        CompareFields: false,
        CompareComments: true,
        CompareMoves: true,
        IgnoreAllComparisonWarnings: true,
        RevisedAuthor: null);

    public static NativeWordCompareOptions WithFormatting { get; } = new(
        CompareFormatting: true,
        CompareCaseChanges: false,
        CompareWhitespace: false,
        CompareTables: true,
        CompareHeaders: true,
        CompareFootnotes: true,
        CompareTextboxes: true,
        CompareFields: false,
        CompareComments: true,
        CompareMoves: true,
        IgnoreAllComparisonWarnings: true,
        RevisedAuthor: null);
}
