# VeyraFlow Architecture

## Layers and Dependencies

```
┌─────────────────────────────────────┐
│         Veyra.Desktop               │  Avalonia 11 UI, MVVM, Navigation
│         (Presentation)              │
└──────────────┬──────────────────────┘
               │ uses
┌──────────────▼──────────────────────┐
│        Veyra.Application            │  Use cases, Commands, Queries, DTOs
│        (Application Layer)          │  MediatR, FluentValidation, AutoMapper
└────┬─────────────────────┬──────────┘
     │ uses                │ uses
┌────▼──────────┐  ┌───────▼──────────────────────────────────────┐
│ Veyra.Domain  │  │  Infrastructure Projects                      │
│ (Core)        │  │  ├─ Veyra.Infrastructure.Data (EF+SQLite)     │
│ Entities,     │  │  ├─ Veyra.Infrastructure.Native (Rust P/Inv.) │
│ Value Objects │  │  ├─ Veyra.Infrastructure.Sync (HTTP/Cloud)    │
└───────────────┘  │  └─ Veyra.Shared.Logging (Serilog)           │
                   └──────────────────────────────────────────────┘
```

**Dependency rule**: Domain knows nothing about Infrastructure. Application depends on Domain and Abstractions. Infrastructure implements interfaces from Application.Abstractions.

---

## Dependency Injection (Desktop)

Modular registration split into 7 files in `src/Veyra.Desktop/CompositionRoot/`:

| File | Registers |
|------|-----------|
| `DependencyInjection.cs` | Entry point, calls all others |
| `DependencyInjection.Runtime.cs` | Scheduler, background services |
| `DependencyInjection.Services.cs` | Application services (navigation, tray, sync) |
| `DependencyInjection.PageViewModels.cs` | Page ViewModels |
| `DependencyInjection.WindowViewModels.cs` | Dialog ViewModels |
| `DependencyInjection.Views.cs` | View registration for ViewLocator |
| `DependencyInjection.DesktopInfrastructure.cs` | Desktop-specific overrides |

---

## Startup Flow

```
Program.Main()
  └─ App.OnFrameworkInitializationCompleted()
       ├─ BuildServiceProvider() — DI composition
       ├─ Initialize SQLite DB (%LOCALAPPDATA%\VeyraFlow\veyra.db)
       ├─ Show MainWindow immediately (fast startup)
       └─ RunDeferredStartupAsync() [background Task]
            ├─ EF Core migrations
            ├─ Native library health check (veyra_core.dll)
            ├─ User profile resolution
            ├─ Cloud restore (if token valid)
            ├─ Sync queue processing
            └─ Start snapshot scheduler
```

---

## CQRS / MediatR Flow

```
ViewModel → IMediator.Send(command/query)
              └─ ValidationBehavior (FluentValidation)
                   └─ LoggingBehavior
                        └─ CommandHandler / QueryHandler
                             └─ Repository / DbContext / NativeService
```

### Typical command structure
```csharp
// Application/Commands/Repository/CreateRepositoryCommand.cs
public record CreateRepositoryCommand(string Name, ...) : IRequest<OperationResult<RepositoryDto>>;

// Application/Commands/Repository/CreateRepositoryHandler.cs  
public class CreateRepositoryHandler : IRequestHandler<CreateRepositoryCommand, OperationResult<RepositoryDto>>

// Application/Commands/Repository/CreateRepositoryValidator.cs
public class CreateRepositoryValidator : AbstractValidator<CreateRepositoryCommand>
```

---

## Infrastructure.Data (EF Core)

- **DbContext**: `VeyraDbContext` — 41+ DbSet<T>
- **Migrations**: `src/Veyra.Infrastructure.Data/Persistence/Migrations/`
- **Configurations**: Each entity has its own `IEntityTypeConfiguration<T>` class
- **Global filters**: Soft delete (`IsDeleted`) applied globally via `HasQueryFilter`
- **Performance**: Dapper used for complex read-heavy queries

### Creating a migration
```powershell
cd src/Veyra.Infrastructure.Data
dotnet ef migrations add <MigrationName> --startup-project ../Veyra.Desktop
dotnet ef database update --startup-project ../Veyra.Desktop
```

---

## Infrastructure.Native (Rust P/Invoke)

- Library: `native/veyra_core/` → compiled to `veyra_core.dll`
- P/Invoke declarations: `src/Veyra.Infrastructure.Native/Interop/`
- SafeHandles for native memory management
- Functions: scanning, block storage, BLAKE3 hash, zstd compress, text diff, image diff, AES-GCM encrypt

### Build native
```powershell
cd native/veyra_core
cargo build --release
# dll appears in native/veyra_core/target/release/veyra_core.dll
# Copied to output directory via MSBuild target
```

---

## Infrastructure.Sync (Cloud)

- `CloudSyncHttpService` — REST client for the Go API
- `AuthHttpService` — OAuth tokens, refresh
- `AccessTokenPolicyService` — token validity check
- Retry policies: Polly with exponential backoff
- Cloud metadata protection: optional metadata encryption before upload

---

## Desktop Navigation

```
MainWindow
  ├─ Dashboard (INavigationService.NavigateTo<RepositoryDashboardViewModel>)
  ├─ Explorer  (INavigationService.NavigateTo<RepositoryExplorerViewModel>)
  ├─ Search    (INavigationService.NavigateTo<GlobalSearchViewModel>)
  └─ Settings  (INavigationService.NavigateTo<AppSettingsViewModel>)

Dialogs are opened via IWindowService.ShowDialog<TViewModel>()
```

---

## Testing

```
tests/
├─ Veyra.Domain.Tests/              ← Entity logic (xunit)
├─ Veyra.Application.Tests/         ← Command/Query handlers
├─ Veyra.Infrastructure.Data.Tests/ ← EF Core, SQLite
└─ Veyra.Desktop.Tests/             ← ViewModels, UI logic
```

Coverage via `coverlet.collector`.
