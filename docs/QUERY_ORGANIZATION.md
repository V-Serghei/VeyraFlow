# Query Organization — Veyra.Application/Queries

## Structure

```
Queries/
├── Repository/          ← namespace: Veyra.Application.Queries.Repository
│   ├── GetAllRepositoriesQuery.cs         ← All repositories
│   ├── GetAllRepositoriesHandler.cs
│   ├── GetRepositoryDetailQuery.cs        ← Single repository by Id
│   ├── GetRepositoryDetailHandler.cs
│   │
│   ├── GetFileVersionHistoryQuery.cs      ← File version history
│   ├── GetFileVersionHistoryHandler.cs
│   ├── GetFileVersionTextContentQuery.cs  ← Text content of a version
│   ├── GetFileVersionTextContentHandler.cs
│   ├── GetFileVersionDiffPreviewQuery.cs  ← Diff between two versions
│   ├── GetFileVersionDiffPreviewHandler.cs
│   ├── GetPendingFileDiffPreviewQuery.cs  ← Diff of current file vs latest version
│   ├── GetPendingFileDiffPreviewHandler.cs
│   │
│   ├── GetRepositoryLatestEntriesQuery.cs ← Current state of all files
│   ├── GetRepositoryLatestEntriesHandler.cs
│   ├── GetRepositoryPendingChangesQuery.cs ← Uncommitted changes
│   ├── GetRepositoryPendingChangesHandler.cs
│   │
│   ├── GetRepositorySnapshotHistoryQuery.cs ← Snapshot history
│   ├── GetRepositorySnapshotHistoryHandler.cs
│   ├── GetRepositorySnapshotChangedFilesQuery.cs ← Files changed in a snapshot
│   ├── GetRepositorySnapshotChangedFilesHandler.cs
│   │
│   ├── GetTextDiffQuery.cs               ← Stored text diff
│   ├── GetTextDiffHandler.cs
│   ├── GetTrackedExtensionsQuery.cs       ← Tracked file extensions
│   ├── GetTrackedExtensionsHandler.cs
│   ├── ValidateRepositoryBundleQuery.cs   ← Export bundle validation
│   └── ValidateRepositoryBundleHandler.cs
│
├── Search/              ← namespace: Veyra.Application.Queries.Search
│   ├── GetGlobalSearchIndexQuery.cs
│   └── GetGlobalSearchIndexHandler.cs
│
└── Security/            ← namespace: Veyra.Application.Queries.Security
    ├── GetArtifactKeyRingQuery.cs
    └── GetArtifactKeyRingHandler.cs
```

---

## Namespaces

| Folder | Namespace |
|--------|-----------|
| `Queries/Repository/` | `Veyra.Application.Queries.Repository` |
| `Queries/Search/` | `Veyra.Application.Queries.Search` |
| `Queries/Security/` | `Veyra.Application.Queries.Security` |

**Note:** `Queries/Repository/` contains all repository-related queries including `GetAllRepositoriesQuery` and `GetRepositoryDetailQuery` — these were previously at the root level and have been moved here.

---

## Grouping by domain

### Repository overview
- `GetAllRepositoriesQuery` — list all repositories
- `GetRepositoryDetailQuery` — details of a single repository

### File versioning
- `GetFileVersionHistoryQuery` — list of versions for a specific file
- `GetFileVersionTextContentQuery` — text content of a version for preview
- `GetFileVersionDiffPreviewQuery` — diff between two versions
- `GetPendingFileDiffPreviewQuery` — diff of current file vs saved version

### Repository state
- `GetRepositoryLatestEntriesQuery` — current state of all files
- `GetRepositoryPendingChangesQuery` — changes since the last snapshot

### Snapshot history
- `GetRepositorySnapshotHistoryQuery` — list of snapshots
- `GetRepositorySnapshotChangedFilesQuery` — changes in a specific snapshot

### Utilities
- `GetTextDiffQuery` — retrieve a stored text diff from the database
- `GetTrackedExtensionsQuery` — tracked file formats
- `ValidateRepositoryBundleQuery` — validate before import

---

## How to add new queries

1. Create files in `Queries/Repository/` (or another subfolder by domain)
2. Namespace: `Veyra.Application.Queries.Repository`
3. Query record: `IRequest<T>` where T is a DTO or nullable DTO
4. Handler: `IRequestHandler<TQuery, T>`
5. Validator (optional): if the query has parameters, add an `AbstractValidator<TQuery>`
6. Desktop caller adds `using Veyra.Application.Queries.Repository;`

---

## Why this structure instead of feature slices

The current structure is **layer-first** (all queries together). The alternative is **feature-first** (query + command + DTO in one folder per feature).

For this project's scale, layer-first is justified:
- 26 queries, all in one project — fits in one folder
- IDE navigation works well (all queries are co-located)
- Handlers are easy to find — file name = query name + "Handler"

Feature-first would become advantageous at 100+ features with cross-feature dependencies.
