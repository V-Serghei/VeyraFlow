# Patterns

## Rust planner with managed fallback

- Keep C# orchestration, EF queries, domain state transitions, and UI progress in managed code.
- Move deterministic calculation cores to Rust behind coarse JSON FFI entrypoints.
- Add an Application abstraction for the calculation, for example `IRepositoryRetentionPlanner`.
- Register a managed implementation in `Veyra.Application` as the default fallback.
- Register the Rust implementation from `Veyra.Infrastructure.Native` after Application/Data DI so normal resolution prefers native while tests and old native DLLs can still use managed behavior.
- Make new native entrypoints optional in `VeyraCoreNative.OptionalEntrypoints` until the packaged DLL is guaranteed to contain them.
- Precompute C#-specific classification flags before crossing FFI when the logic depends on existing C# classifiers or tag normalization. This avoids duplicating fragile business classification logic in Rust.
