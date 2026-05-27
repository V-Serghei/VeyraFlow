# Testing Structure

All test projects live under `/tests`.

```text
tests/
  Veyra.Domain.Tests/
  Veyra.Application.Tests/
  Veyra.Infrastructure.Data.Tests/
  Veyra.Desktop.Tests/
```

## Project Responsibilities

| Project | Responsibility | Dependencies |
|---|---|---|
| `Veyra.Domain.Tests` | Domain invariants and pure domain logic | Domain only |
| `Veyra.Application.Tests` | Handlers and Application services with fakes | Application |
| `Veyra.Infrastructure.Data.Tests` | EF/SQLite persistence integration tests | Infrastructure.Data |
| `Veyra.Desktop.Tests` | ViewModel/UI-service tests and Desktop quality gates | Desktop + Application |

## Desktop Test Boundary

`Veyra.Desktop.Tests` should not add new `Infrastructure.Data` coupling.

There are no Infrastructure.Data exceptions in `Veyra.Desktop.Tests`. Cloud sync persistence quality gates live in `Veyra.Infrastructure.Data.Tests`, and concrete SVG structural diff tests also live with the data/preview infrastructure.

## Architecture Quality Gate

`ArchitectureDependencyRulesTests` enforces:

- Desktop UI files do not reference `Veyra.Infrastructure.*`.
- Desktop UI files do not reference EF Core or SQLite directly.
- New Desktop tests do not add Infrastructure.Data references outside the known transitional integration gate.

If this test fails, prefer adding an Application abstraction or Desktop adapter instead of expanding the allow-list.

## Test Placement Rules

- Domain behavior: `Veyra.Domain.Tests`.
- Application handler with fakes: `Veyra.Application.Tests`.
- EF query/update behavior: `Veyra.Infrastructure.Data.Tests`.
- ViewModel behavior: `Veyra.Desktop.Tests`.
- Native adapter behavior: use a native/infrastructure test project, not Desktop tests.
