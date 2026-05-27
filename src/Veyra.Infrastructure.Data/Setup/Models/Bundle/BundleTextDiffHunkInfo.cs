namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleTextDiffHunkInfo
{
    public long Id { get; set; }
    public long DiffId { get; set; }
    public int Sequence { get; set; }
    public int StartLineSequence { get; set; }
    public int EndLineSequence { get; set; }
    public int OldStartLine { get; set; }
    public int OldLineCount { get; set; }
    public int NewStartLine { get; set; }
    public int NewLineCount { get; set; }
    public string ChangeKind { get; set; } = "modified";
    public DateTime CreatedAtUtc { get; set; }
}
