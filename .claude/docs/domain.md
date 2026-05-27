# Domain Entities

## Aggregate Roots

### Repository
Main aggregate — a directory under versioning.
```csharp
// src/Veyra.Domain/Entities/Repository.cs
Id, Name, RootPath, CreatedAt, UpdatedAt
IsDeleted, DeletedAt  // soft delete
ExclusionPatterns     // JSON list
FileCount, VersionCount, SnapshotCount
TotalSizeBytes, DeduplicatedSizeBytes
RetentionPolicy (MaxAge, MaxSnapshots, MaxSizeBytes)
CloudSyncEnabled, CloudSyncConflictStrategy
SyncRetryCount, LastSyncAt
```

### RepositorySnapshot
A point in time.
```csharp
// src/Veyra.Domain/Entities/RepositorySnapshot.cs
Id, RepositoryId (FK), CreatedAt
Title, Tags (JSON list)
TriggerType: Manual | Automatic | Scheduled
TotalEntries, TotalFiles, TotalBytes
IsArchived, ArchivedAt
```

---

## File Versioning Hierarchy

```
Repository
  └─ FileIdentity (unique path within the repo)
       └─ FileVersion (a specific version of the file)
            └─ FileVersionBlock (deduplication block)
```

### FileIdentity
```csharp
Id, RepositoryId, RelativePath
FileName, Extension
```
Unique index: `(RepositoryId, RelativePath)`

### FileVersion
```csharp
Id, FileIdentityId, SnapshotId
ContentHash (SHA-256)
SizeBytes, LastWriteTime
IsDeleted (file was deleted in this version)
CreatedAt
```

### FileVersionBlock
```csharp
Id, FileVersionId
BlockStorageKey (BLAKE3 hash)
SequenceNumber
StoredSizeBytes, OriginalSizeBytes
CompressionRatio
```

---

## Diff Entities

```csharp
// FileVersionTextDiff
Id, FileVersionId, PreviousVersionId
LinesAdded, LinesRemoved

// FileVersionTextDiffHunk  
Id, DiffId
OldStart, OldCount, NewStart, NewCount

// FileVersionTextDiffLine
Id, HunkId
LineType: Context | Added | Removed
OldLineNumber, NewLineNumber
Content

// TextLineAtom
Id, LineId
AtomType: Equal | Insert | Delete
Content
```

---

## Operational Entities

### OperationJournalEntry
```csharp
Id, RepositoryId, CreatedAt
OperationType (enum)
Status: Success | Failure | Warning
Details (JSON)
DurationMs
```

### RepositorySyncQueueItem
```csharp
Id, RepositoryId
ItemType: Snapshot | FileVersion | Block | Metadata
Status: Pending | InProgress | Completed | Failed
RetryCount, LastAttemptAt
ConflictStrategy
Payload (JSON)
```

### UserProfile
```csharp
Id, Email, DisplayName
AccessToken, RefreshToken
TokenExpiresAt
CreatedAt, LastLoginAt
```

---

## Watched Directories

```csharp
// src/Veyra.Domain/Entities/Watched/
WatchedDirectory     → directory under observation
WatchedFormat        → tracked file formats
WatchedExclusionRule → exclusion rules
```

---

## Key Indexes (SQLite)

- `repository.root_path` — UNIQUE (two repos cannot share the same directory)
- `file_identity.(repository_id, relative_path)` — UNIQUE
- `repository_snapshot.(repository_id, created_at)` — fast history lookup
- `file_version.(file_identity_id, created_at)` — version timeline
- `file_version_block.block_storage_key` — deduplication
