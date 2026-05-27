# Solution Structure

## Projects

```text
src/
  Veyra.Domain/              Domain entities and domain-only constants.
  Veyra.Application/         Use cases, DTOs, interfaces, MediatR handlers.
  Veyra.Infrastructure.Data/ EF Core, SQLite, persistence implementations.
  Veyra.Infrastructure.Native/ Rust/native adapters.
  Veyra.Infrastructure.Sync/ Cloud HTTP client implementations.
  Veyra.Desktop/             Avalonia UI, ViewModels, UI services.
  Veyra.Shared.Logging/      Shared logging setup.

tests/
  Veyra.Domain.Tests/
  Veyra.Application.Tests/
  Veyra.Infrastructure.Data.Tests/
  Veyra.Desktop.Tests/
```

## Dependency Direction

Target dependency direction:

```text
Desktop UI -> Application -> Domain
Infrastructure.Data -> Application -> Domain
Infrastructure.Native -> Application -> Domain
Infrastructure.Sync -> Application -> Domain
```

`Domain` must not reference any other Veyra project.

`Application` must not reference Desktop, EF Core implementations, native implementations, or cloud HTTP implementations.

`Infrastructure.*` implements Application abstractions.

`Desktop` ViewModels, Views, tray presenters, navigation services, and UI helpers must use Application commands/queries or Desktop-only UI abstractions.

## Transitional Desktop Exceptions

The current application still has a composition-root seam inside `Veyra.Desktop`. This file may reference Infrastructure until a dedicated bootstrap/composition project or host project is introduced:

- `src/Veyra.Desktop/CompositionRoot/DependencyInjection.cs`

No new UI/ViewModel code should add `Veyra.Infrastructure.*`, EF Core, SQLite, or native implementation references.

## Cleanup Roadmap

1. Introduce a composition/bootstrap host project if the app must strictly remove Infrastructure project references from `Veyra.Desktop.csproj`.
2. Move `DependencyInjection.BuildServiceProvider` to that host project.
3. Remove the final transitional allow-list entry from `ArchitectureDependencyRulesTests`.
