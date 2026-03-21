namespace Veyra.Desktop.ViewModels.Windows;

public sealed class SnapshotDiffTextSegmentViewModel
{
    public string Text { get; init; } = string.Empty;
    public string Foreground { get; init; } = "#EAF2FF";
    public string Background { get; init; } = "Transparent";
    public bool IsEmphasized { get; init; }
}
