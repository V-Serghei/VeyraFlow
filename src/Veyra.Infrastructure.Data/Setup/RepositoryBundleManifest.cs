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

internal sealed class BundleRepositoryInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int FileCount { get; set; }
    public int VersionCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime? LastScannedAtUtc { get; set; }
    public bool AutoCaptureFileVersions { get; set; }
    public bool ProtectCloudMetadata { get; set; } = true;
}

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

internal sealed class BundleSnapshotInfo
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int TotalEntries { get; set; }
    public int FileEntries { get; set; }
    public int DirectoryEntries { get; set; }
    public long TotalFileBytes { get; set; }
}

internal sealed class BundleSnapshotEntryInfo
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string? ParentRelativePath { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string? Extension { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string? ContentHashSha256 { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed class BundleFileIdentityInfo
{
    public long Id { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Extension { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class BundleFileVersionInfo
{
    public long Id { get; set; }
    public long FileIdentityId { get; set; }
    public string ContentHashSha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public bool IsDeletionMarker { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed class BundleFileVersionBlockInfo
{
    public long Id { get; set; }
    public long FileVersionId { get; set; }
    public int Sequence { get; set; }
    public string BlockHashBlake3 { get; set; } = string.Empty;
    public int LengthBytes { get; set; }
    public long StoredSizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed class BundleSnapshotLinkInfo
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public long FileIdentityId { get; set; }
    public long FileVersionId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed class BundleTextDiffInfo
{
    public long Id { get; set; }
    public long LeftFileVersionId { get; set; }
    public long RightFileVersionId { get; set; }
    public int MaxLines { get; set; }
    public string DiffKeySha256 { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
    public bool IsTruncated { get; set; }
    public int StorageFormatVersion { get; set; }
    public string LinesJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

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

internal sealed class BundleTextLineAtomInfo
{
    public long Id { get; set; }
    public string HashSha256 { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed class BundleBlockArtifactInfo
{
    public string Hash { get; set; } = string.Empty;
    public string EntryName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}
