namespace Veyra.Infrastructure.Data.Setup;

internal sealed class BundleStatsInfo
{
    public int SnapshotCount { get; set; }
    public int SnapshotEntryCount { get; set; }
    public int FileIdentityCount { get; set; }
    public int FileVersionCount { get; set; }
    public int FileVersionBlockCount { get; set; }
    public int SnapshotFileLinkCount { get; set; }
    public int TextDiffCount { get; set; }
    public int TextDiffHunkCount { get; set; }
    public int TextDiffLineCount { get; set; }
    public int TextLineAtomCount { get; set; }
    public int BlockArtifactCount { get; set; }
}
