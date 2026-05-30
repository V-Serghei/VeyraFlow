# MEMORY

## 2026-05-30

- Fixed `Veyra.Domain` build failure `CS0101` caused by duplicate entity files left in `src/Veyra.Domain/Entities/` after the domain model was reorganized into `Entities/Repository/` and `Entities/FileVersioning/`.
- Removed stale root duplicates of `FileSnapshot`, `Repository`, `RepositorySnapshot`, and `RepositorySnapshotEntry`; the canonical definitions are the versions inside the bounded-context subfolders.
- Fixed the same stale-root-copy pattern in `Veyra.Application`, `Veyra.Infrastructure.Data`, and `Veyra.Desktop`.
- `SetupWizard` kept the root UI files as canonical and absorbed the newer localized `Steps` content; `WelcomeWindow`, `Windows/Shell`, `Dashboard/Items`, and `Explorer/Items` kept the newer nested files and dropped stale root duplicates.
- Full solution build now succeeds with `.NET SDK 10.0.300`; remaining output is limited to `NU1903` warnings for `Tmds.DBus.Protocol` `0.21.2` in `Veyra.Desktop` and `Veyra.Desktop.Tests`.
- Updated `.editorconfig` to prefer `var` for built-in local types too (`csharp_style_var_for_built_in_types = true`), so Rider stops suggesting explicit `int`/`string`/`bool` in obvious local declarations.
- Updated `.editorconfig` to prefer `var` everywhere for locals (`csharp_style_var_elsewhere = true`) so method-call assignments like `var repo = await repositories.GetRepositoryByIdAsync(...)` are treated as the preferred style too.
- Traced the remaining `NU1903` warning to Avalonia's Linux desktop chain in the desktop restore graph: `Avalonia.Desktop` -> `Avalonia.X11` -> `Avalonia.FreeDesktop` -> `Tmds.DBus.Protocol 0.21.2`.
- Filled the previously empty `AddWatchedDirectoryCommandValidator`, aligned it with the watched-directory validation rules, and made `AddWatchedDirectoryCommand` concrete so it can be instantiated and validated.
- Removed `CS8629` in `RepositoryCloudSyncOrchestrator` by avoiding `.Value` on `remoteLatestSnapshotId` inside the manual-conflict branch and using a local `GetValueOrDefault()` value for the error message.
- Reconfirmed the current cross-language split: `native/veyra_core` already owns local hash/scan/block-store/compress/diff/image-diff work, while `server/cloud-api` already owns snapshot/block sync endpoints and pack/block storage flows; future moves should keep `C#` as orchestration/UI/domain glue and push only coarse-grained deterministic CPU/IO logic to `Rust` or authoritative multi-client cloud workflows to `Go`.
- Concrete next migration candidates:
  - `Rust`: retention/integrity/archive/recovery calculation cores behind coarse JSON FFI payloads.
  - `Go`: cloud retention/GC/pack compaction/restore-package preparation as authoritative server jobs.
  - `C#`: keep MediatR/UI/EF/domain orchestration and invoke `Rust`/`Go` as engines rather than moving application workflows wholesale.
- Started the first Rust migration candidate: repository retention planning now goes through `IRepositoryRetentionPlanner`; `ManagedRepositoryRetentionPlanner` is the safe C# fallback and `RustRepositoryRetentionPlanner` calls optional native entrypoint `veyra_plan_retention_snapshots_utf8` when available.
- Native feature diagnostics now include `retention_planning`; old native DLLs remain compatible because the new entrypoint is optional, not required for native runtime health.
- `cargo check` validates `native/veyra_core` retention code, but `cargo test` needs MSVC `link.exe` in PATH. In this sandbox, full Rust tests stopped at linker discovery rather than code errors.
- Desktop builds inside the sandbox can fail in Avalonia telemetry with denied access to `%LOCALAPPDATA%\AvaloniaUI\BuildServices\buildtasks.log`; set `AVALONIA_TELEMETRY_OPTOUT=1` for build verification in restricted sessions.
