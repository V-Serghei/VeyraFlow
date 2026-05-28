# Snapshot Pipeline

VeyraFlow treats local snapshot creation as the primary operation. Cloud sync is a secondary background stage and must never block local commit.

## Pipeline Stages

```mermaid
flowchart LR
    A["Scan stage"] --> B["Change detection"]
    B --> C["Dedup / version planning"]
    C --> D["Block persistence"]
    D --> E["Metadata persistence"]
    E --> F["Snapshot commit"]
    F --> G["Post-processing"]
    G --> H["Cloud queue stage"]
    H -. "optional background work" .-> I["Cloud sync worker"]
```

| Stage | Responsibility | Cloud dependency |
|---|---|---|
| Scan | Read repository tree and tracked formats | None |
| Change detection | Compare current entries with latest local baseline | None |
| Dedup / version planning | Decide whether new `FileVersion` rows are needed | None |
| Block persistence | Store local content blocks | None |
| Metadata persistence | Write snapshot entries, identities, versions, links | None |
| Snapshot commit | Commit SQLite transaction | None |
| Post-processing | Precompute local text diffs where possible | None |
| Cloud queue | Add a persistent queue item for later upload | No HTTP required |

## Contract

- A local snapshot is complete when the SQLite transaction commits.
- Queueing cloud work must be fast and local.
- Token refresh, HTTP push, remote conflict checks, and block upload happen only after local commit.
- Cloud errors update sync status, but they do not roll back or delay the snapshot.

## Progress Model

Progress should be phase-based instead of fake ETA-based:

- `prepare`: repository metadata and options are loaded.
- `scan`: files are enumerated and hashed.
- `save`: local snapshot data is persisted.
- `save_versions`: file versions and blocks are prepared.
- `save_persist_versions`: version metadata is committed.
- `save_links`: snapshot/file links are written.
- `save_diff_precompute`: local diff cache is prepared.
- `done`: local work is complete; cloud sync may be queued separately.

If ETA is unreliable, show current phase, processed files, processed blocks, and throughput instead of “1 second remaining”.

