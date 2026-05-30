# Gotchas

## 2026-05-30 - Duplicate domain entity files after folder reorganization

- `Veyra.Domain` compiles every `.cs` file under `src/Veyra.Domain` by default, so moving entities into bounded-context folders without removing the old root copies creates `CS0101` duplicate-type errors.
- The canonical files for these entities live in:
  - `src/Veyra.Domain/Entities/Repository/`
  - `src/Veyra.Domain/Entities/FileVersioning/`
- If `FileSnapshot`, `Repository`, `RepositorySnapshot`, or `RepositorySnapshotEntry` appear both in `Entities/` and in a subfolder, delete the stale root copies instead of trying to rename namespaces, because all domain entities intentionally stay in `namespace Veyra.Domain.Entities;`.

## 2026-05-30 - Stale root copies after feature-folder migration

- The same migration mistake also existed in `Veyra.Application`, `Veyra.Infrastructure.Data`, and `Veyra.Desktop`: old root files were left beside newer files in `Repository/`, `Scanning/`, `Auth/UserProfiles/`, `Setup/Repositories/`, `Setup/Snapshots/`, `Persistence/Context/`, `Windows/Shell/`, `Dashboard/Items/`, `Explorer/Items/`, and `WelcomeWindow/Steps/`.
- In this solution, default SDK item globs compile every `.cs` file, and Avalonia compiles every `.axaml` file, so duplicated files with the same namespace and `x:Class` cause:
  - `CS0101`, `CS0111`, `CS0535`, `CS8863` for C#
  - `AVLN2002` for Avalonia XAML
- The safe fix pattern is:
  - keep the richer nested version,
  - remove the stale root copy,
  - only merge nested content back into the root path when the root path is the intended canonical UI file location.
