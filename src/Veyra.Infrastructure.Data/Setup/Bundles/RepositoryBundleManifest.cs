namespace Veyra.Infrastructure.Data.Setup;

internal sealed class RepositoryBundleManifest
{
    public int BundleFormatVersion { get; set; }
    public string CreatedBy { get; set; } = "veyra-desktop";
    public string SourceProductVersion { get; set; } = string.Empty;
    public DateTime ExportedAtUtc { get; set; }
    public BundleRepositoryInfo Repository { get; set; } = new();
    public BundleStatsInfo Stats { get; set; } = new();
    public List<BundleSnapshotInfo> Snapshots { get; set; } = [];
    public List<BundleSnapshotEntryInfo> SnapshotEntries { get; set; } = [];
    public List<BundleFileIdentityInfo> FileIdentities { get; set; } = [];
    public List<BundleFileVersionInfo> FileVersions { get; set; } = [];
    public List<BundleFileVersionBlockInfo> FileVersionBlocks { get; set; } = [];
    public List<BundleSnapshotLinkInfo> SnapshotFileLinks { get; set; } = [];
    public List<BundleTextDiffInfo> TextDiffs { get; set; } = [];
    public List<BundleTextDiffHunkInfo> TextDiffHunks { get; set; } = [];
    public List<BundleTextDiffLineInfo> TextDiffLines { get; set; } = [];
    public List<BundleTextLineAtomInfo> TextLineAtoms { get; set; } = [];
    public List<BundleBlockArtifactInfo> BlockArtifacts { get; set; } = [];
}
