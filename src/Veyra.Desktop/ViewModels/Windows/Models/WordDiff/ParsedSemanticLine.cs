namespace Veyra.Desktop.ViewModels.Windows;

internal sealed record ParsedSemanticLine(
    WordSemanticLineKind Kind,
    WordStyleInfo Style,
    string Text,
    string Raw)
{
    public static ParsedSemanticLine None { get; } = new(WordSemanticLineKind.None, WordStyleInfo.Default, string.Empty, string.Empty);
}
