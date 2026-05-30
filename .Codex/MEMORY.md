# MEMORY

## 2026-05-30

- Fixed `Veyra.Domain` build failure `CS0101` caused by duplicate entity files left in `src/Veyra.Domain/Entities/` after the domain model was reorganized into `Entities/Repository/` and `Entities/FileVersioning/`.
- Removed stale root duplicates of `FileSnapshot`, `Repository`, `RepositorySnapshot`, and `RepositorySnapshotEntry`; the canonical definitions are the versions inside the bounded-context subfolders.
- Fixed the same stale-root-copy pattern in `Veyra.Application`, `Veyra.Infrastructure.Data`, and `Veyra.Desktop`.
- `SetupWizard` kept the root UI files as canonical and absorbed the newer localized `Steps` content; `WelcomeWindow`, `Windows/Shell`, `Dashboard/Items`, and `Explorer/Items` kept the newer nested files and dropped stale root duplicates.
- Full solution build now succeeds with `.NET SDK 10.0.300`; remaining output is limited to `NU1903` warnings for `Tmds.DBus.Protocol` `0.21.2` in `Veyra.Desktop` and `Veyra.Desktop.Tests`.
- Updated `.editorconfig` to prefer `var` for built-in local types too (`csharp_style_var_for_built_in_types = true`), so Rider stops suggesting explicit `int`/`string`/`bool` in obvious local declarations.
