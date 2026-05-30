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

## 2026-05-30 - `NU1903` for `Tmds.DBus.Protocol` in Desktop builds

- `Veyra.Desktop` does not reference `Tmds.DBus.Protocol` directly; the warning comes from the Avalonia desktop Linux stack:
  - `Avalonia.Desktop` -> `Avalonia.X11` -> `Avalonia.FreeDesktop` -> `Tmds.DBus.Protocol`
- Current local restore resolved `Tmds.DBus.Protocol` to `0.21.2`, which NuGet flags with `NU1903` because of advisory `GHSA-xrw6-gwf8-vvr9` / `CVE-2026-39959`.
- `Veyra.Desktop.Tests` shows the same warning because it references `Veyra.Desktop`, so it inherits the same restore graph.
- Fastest mitigation is to add an explicit direct package reference to a patched version of `Tmds.DBus.Protocol` (`0.21.3` or newer) in the desktop project, or upgrade Avalonia to a line that already requires a patched version.

## 2026-05-30 - Avalonia telemetry task can fail in sandboxed builds

- `AvaloniaStatsTask` may try to write `%LOCALAPPDATA%\AvaloniaUI\BuildServices\buildtasks.log`.
- In Codex workspace sandbox this can throw `UnauthorizedAccessException`, even when all project code has already compiled.
- For verification builds, set `AVALONIA_TELEMETRY_OPTOUT=1` before `dotnet build`:
  - PowerShell: `$env:AVALONIA_TELEMETRY_OPTOUT='1'; & 'C:\Users\visto\.dotnet\dotnet.exe' build src\Veyra.Desktop\Veyra.Desktop.csproj --no-restore`

## 2026-05-30 - Rust module ambiguity from empty root module file

- Rust module resolution treats both `src/hash.rs` and `src/hash/mod.rs` as definitions for `pub mod hash;`.
- Keeping both files causes `E0761: file for module 'hash' found at both ...`.
- The real implementation is `native/veyra_core/src/hash/mod.rs`; the empty `native/veyra_core/src/hash.rs` should stay removed.
