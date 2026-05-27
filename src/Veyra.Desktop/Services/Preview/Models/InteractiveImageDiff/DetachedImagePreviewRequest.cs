namespace Veyra.Desktop.Services.Preview;

public sealed record DetachedImagePreviewRequest
{
    public string Title { get; init; } = string.Empty;
    public string? LeftImagePath { get; init; }
    public string? RightImagePath { get; init; }
    public byte[] OverlayPngBytes { get; init; } = [];
    public bool IsSplitMode { get; init; }
    public double InitialSplitPercent { get; init; } = 50d;
}
