# VeyraFlow — Claude Code Knowledge Base

> This file is loaded automatically on every Claude Code session in this project.
> Detailed documentation is in `.claude/docs/`. Slash commands are in `.claude/commands/`.

---

## Quick Start for AI Agent

**Project**: VeyraFlow — desktop file manager with versioning, block deduplication and cloud sync.  
**Stack**: .NET 10 · Avalonia 11 · SQLite + EF Core · Rust (native) · Go (cloud API)  
**Architecture**: Clean Architecture + CQRS (MediatR)

### Key Directories
```
src/Veyra.Desktop/          ← Avalonia UI (MVVM, navigation, services)
src/Veyra.Application/      ← Use cases (Commands, Queries, DTOs, Validation)
src/Veyra.Domain/           ← Entities organized by bounded context:
                                  Entities/Repository/    ← Repository, Snapshot, SyncQueue
                                  Entities/FileVersioning/ ← FileIdentity, FileVersion, Block
                                  Entities/TextDiff/       ← FileVersionTextDiff, Hunk, Line
                                  Entities/Watched/        ← WatchedDirectory, Format
src/Veyra.Infrastructure.Data/    ← EF Core + SQLite
src/Veyra.Infrastructure.Native/  ← P/Invoke → Rust dll
src/Veyra.Infrastructure.Sync/    ← HTTP cloud client (Go API)
native/veyra_core/          ← Rust: BLAKE3, zstd, AES-GCM, diff
server/cloud-api/           ← Go REST API (PostgreSQL)
tests/                      ← xunit, 4 projects (Domain, Application, Infrastructure.Data, Desktop)
```

**Note:** All domain entities use `namespace Veyra.Domain.Entities;` regardless of subfolder.

---

## Build

```powershell
# Desktop only (fast, no dependencies)
dotnet build src/Veyra.Desktop/Veyra.Desktop.csproj --no-dependencies

# Full .NET build
dotnet build VeyraFlow.sln

# Rust native library
cd native/veyra_core && cargo build --release

# Cloud API (Go)
cd server/cloud-api && go build ./cmd/api
```

## Run

```powershell
dotnet run --project src/Veyra.Desktop/Veyra.Desktop.csproj
# Cloud API (default localhost:8080)
cd server/cloud-api && go run ./cmd/api
```

## Tests

```powershell
dotnet test VeyraFlow.sln
cd native/veyra_core && cargo test
```

---

## Custom Commands (slash commands)

| Command | Description |
|---------|-------------|
| `/build` | Build Desktop, show errors |
| `/test` | Run .NET tests |
| `/fix-errors` | Diagnose and fix build errors |
| `/new-command` | Scaffold a new MediatR Command + Handler |
| `/new-query` | Scaffold a new MediatR Query + Handler |
| `/migrate` | Create an EF Core migration |
| `/check-avalonia` | Check Avalonia namespace issues |
| `/add-localization` | Add a new localization key to en.json + ru.json |

---

## Critical Rules

### Avalonia namespace gotchas
- `ScrollBarVisibility` → `Avalonia.Controls.Primitives` (NOT `Avalonia.Controls`)
- `ScrollViewer` → `Avalonia.Controls`
- `CornerRadius`, `Thickness` → `Avalonia` (base namespace)
- When adding converters always verify the full type namespace via NuGet source

### EF Core / SQLite
- Migrations are created from `Veyra.Infrastructure.Data`
- `VeyraDbContext` contains 41+ DbSet
- Soft delete via global filter `IsDeleted`
- Use Dapper for bulk queries, not EF Core

### CQRS / MediatR
- Commands: `IRequest<OperationResult<T>>` or `IRequest<OperationResult>`
- Queries: `IRequest<T>` where T is a DTO or list
- Always add a FluentValidation validator when creating a command/query
- Pipeline behaviors: Validation → Logging → Handler

### Domain Rules
- `Repository` — aggregate root for directories
- `RepositorySnapshot` — point in time (manual/automatic/scheduled)
- `FileVersionBlock` — deduplication block (key = BLAKE3 hash)
- Never mutate domain entities directly from Infrastructure

### Desktop / MVVM
- ViewModels inherit `ObservableObject` (CommunityToolkit.Mvvm)
- Navigation via `INavigationService` (not directly)
- Dialogs via `IWindowService`
- Localization via `Loc.T("key")` or `TrExtension` in XAML

---

## Detailed Documentation

### Agent docs (`.claude/docs/`) — quick reference
- [Architecture](.claude/docs/architecture.md) — layers, dependencies, DI
- [Domain entities](.claude/docs/domain.md) — all entities and their relations
- [Offline & guest mode](.claude/docs/offline.md) — offline-first behavior, guest mode rules, cloud availability, CanSyncToCloud, connectivity service
- [Code patterns](.claude/docs/patterns.md) — command, query, VM templates
- [Known issues](.claude/docs/gotchas.md) — common errors and fixes
- [Avalonia tips](.claude/docs/avalonia.md) — UI framework specifics

### Architecture docs (`docs/`) — full reference
- [SOLUTION_STRUCTURE.md](docs/SOLUTION_STRUCTURE.md) — project layout, tech stack, dependency directions
- [DOMAIN_STRUCTURE.md](docs/DOMAIN_STRUCTURE.md) — entity organization, bounded contexts, invariants
- [APPLICATION_STRUCTURE.md](docs/APPLICATION_STRUCTURE.md) — commands, queries, abstractions, DTOs
- [QUERY_ORGANIZATION.md](docs/QUERY_ORGANIZATION.md) — all queries by domain group, naming rules
- [ARCHITECTURE_PATTERNS.md](docs/ARCHITECTURE_PATTERNS.md) — full pattern audit: used, missing, recommendations
- [TESTING_STRUCTURE.md](docs/TESTING_STRUCTURE.md) — test projects, conventions, gotchas
- [FEATURE_ORGANIZATION.md](docs/FEATURE_ORGANIZATION.md) — layer-first vs feature-first analysis, bounded contexts

---

## Memory and Knowledge Maintenance — MANDATORY

> **This is not optional. Every session must leave a trace.**

### Rule: "learned → recorded"
After every session where you learned something new about the project — update the knowledge immediately:
1. Update the relevant file in `.claude/docs/`
2. Recurring error → add to `.claude/docs/gotchas.md`
3. New pattern → add to `.claude/docs/patterns.md`
4. New command → create `.claude/commands/<name>.md`
5. Update `MEMORY.md` (agent memory index) in `.claude/` or in `C:\Users\visto\.claude\projects\...`

### Rule: "skills always up to date"
- If you implemented a new feature — document how it works in `.claude/docs/`
- If you found a better way to do something in this project — replace the old approach
- If you used a new slash command or pattern — add it to the commands table above
- Goal: the next AI agent must have the full picture without extra questions

### What to always record
- New UI components and their location in code
- Localization changes (keys → files)
- Non-obvious XAML / Avalonia solutions
- ViewModel properties that turned out to be critical
- Any "why is it done this way" — explanations for non-obvious decisions
