# Background Cloud Sync

Cloud sync is always a background process. Local operations are first-class and must never wait for cloud availability.

## Local-First Contract

The local engine must complete these operations without cloud:

- Repository creation.
- Initial snapshot.
- Manual snapshot.
- File browsing.
- History browsing.
- Settings save.
- Retention cleanup.
- Local restore.
- Integrity verification.

Cloud work begins only after local commit. The cloud queue receives work and processes it asynchronously.

## Automatic Sync

Automatic sync must not:

- Block UI.
- Block snapshot creation.
- Block file browsing.
- Block settings.
- Show a blocking full-screen modal.
- Retry aggressively while offline.
- Hold local transactions open while doing HTTP work.

If cloud is unavailable:

- Queue items move to waiting/retry state.
- The connectivity circuit breaker suppresses HTTP work.
- UI shows degraded status.
- Local workflows continue.

## Required Progress Model

Cloud sync progress must report:

- Operation id.
- Repository id.
- Snapshot id when known.
- Phase: discovery, metadata upload, block upload, metadata download, block download, finalization.
- Processed snapshots.
- Uploaded/downloaded blocks.
- Uploaded/downloaded bytes.
- Skipped blocks.
- Retry count.
- Throughput.
- ETA only when reliable.

## Performance Rules

- Use manifest diff before transfer.
- Skip already existing blocks.
- Batch metadata upload.
- Batch block upload and download.
- Use resumable checkpoints.
- Use bounded concurrency.
- Avoid duplicate cloud requests.
- Never restart from zero after a retryable failure.

