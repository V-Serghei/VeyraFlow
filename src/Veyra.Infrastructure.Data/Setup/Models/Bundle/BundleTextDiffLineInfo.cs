namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleTextDiffLineInfo
{
    public long Id { get; set; }
    public long DiffId { get; set; }
    public int Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int? LeftLineNumber { get; set; }
    public int? RightLineNumber { get; set; }
    public long? HunkId { get; set; }
    public int? InHunkSequence { get; set; }
    public long TextLineAtomId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
