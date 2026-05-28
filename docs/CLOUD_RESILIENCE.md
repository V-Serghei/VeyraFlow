# Cloud Resilience

Cloud sync is isolated behind a persistent queue and a cloud availability gate.

## Availability Gate

`ICloudAvailabilityService` wraps `IConnectivityStatusService` and exposes a sync-specific decision:

```csharp
bool ShouldSkipCloudOperation(out string reason);
```

Cloud code must call this before token refresh or HTTP work. If the gate says to skip, the operation updates local sync status and returns without network calls.

## States

| State | Meaning | HTTP allowed |
|---|---|---|
| `Online` | Cloud endpoint is reachable | Yes |
| `Offline` | Local network is unavailable | No |
| `CloudUnavailable` | Network exists, cloud endpoint is down/timed out | No |
| `Unauthorized` | Auth was rejected recently | No, short cool-down |
| `Reconnecting` | Circuit breaker is open after failures | No |
| `Maintenance` | Reserved for future server maintenance signal | No |
| `Unknown` | No trusted availability state yet | No |

## Circuit Breaker

Connectivity failures open a local circuit breaker:

- First failure opens for about 2 minutes.
- Repeated failures back off up to 15 minutes.
- While open, sync queue processing is skipped before token refresh.
- A successful cloud queue item closes the circuit.

## Retry Policy

Queue item retry still uses repository retry settings, but retries are connectivity-aware:

1. If the gate is closed, no HTTP retry is attempted.
2. Queued repositories are marked `offline_retry`.
3. The next explicit queue processing run can resume after connectivity returns.
4. Auth failures are not retried as normal network failures.

## Local Safety

Cloud sync failures never invalidate local snapshots. A failed push only affects `RepositorySyncQueueItem` and repository cloud status fields.

