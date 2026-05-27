# Connectivity State Machine

VeyraFlow has two layers of connectivity state.

## Base Connectivity

`IConnectivityStatusService` probes the environment:

```mermaid
stateDiagram-v2
    [*] --> Unknown
    Unknown --> Online: HEAD probe succeeds
    Unknown --> InternetUnavailable: no network / DNS failure
    Unknown --> CloudUnavailable: timeout / refused
    Online --> CloudUnavailable: endpoint fails
    CloudUnavailable --> Online: probe succeeds
    InternetUnavailable --> Online: network restored
```

## Cloud Availability

`ICloudAvailabilityService` turns base connectivity plus recent sync failures into a cloud-operation decision:

```mermaid
stateDiagram-v2
    [*] --> Unknown
    Unknown --> Online: trusted connectivity snapshot
    Online --> Reconnecting: transient HTTP failure
    Online --> Unauthorized: refresh rejected
    Reconnecting --> Online: successful cloud queue item
    Reconnecting --> CloudUnavailable: base probe still failing
    Unauthorized --> Unknown: short auth cool-down expires
    CloudUnavailable --> Online: base probe succeeds
    Offline --> Online: network returns
```

## Implementation Rule

Every cloud entrypoint must check `ShouldSkipCloudOperation` before:

- token refresh
- listing cloud repositories
- probing remote blocks
- pushing snapshot metadata
- uploading/downloading blocks
- processing pending queue items

Local scan and snapshot code must not call HTTP services directly.

