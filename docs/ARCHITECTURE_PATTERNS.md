# Architecture Patterns — VeyraFlow

Full pattern audit for the project. Each section covers: where it is applied, why it exists, strengths, known problems, and recommendations.

---

## Patterns currently used in the project

### 1. Clean Architecture (Layered)

**Where:** The entire solution structure — Domain → Application → Infrastructure → Desktop.

**Why:** Isolates domain logic from infrastructure. Domain and Application have no dependencies on EF Core, Avalonia, or HTTP clients.

**Implementation:**
- `Veyra.Domain` — pure entity classes, no external dependencies
- `Veyra.Application` — abstractions (interfaces), use cases (commands/queries), DTOs
- `Veyra.Infrastructure.*` — implementations of abstractions (EF Core, P/Invoke, HTTP)
- `Veyra.Desktop` — UI layer, MVVM, navigation

**Strengths:** The Rust native layer or cloud HTTP client can be replaced without touching Application/Domain.

**Problems:**
- `Veyra.Desktop` directly references several Infrastructure projects (violation — UI should not know about infrastructure)
- `Veyra.Desktop.Tests` references `Veyra.Infrastructure.Data` — desktop tests reach deeper than necessary

---

### 2. CQRS (Command Query Responsibility Segregation)

**Where:** `Veyra.Application/Commands/` and `Veyra.Application/Queries/`

**Why:** Separates write operations (commands) from read operations (queries). Allows independent scaling and testing.

**Implementation via MediatR:**
- Commands: `IRequest<OperationResult<T>>` or `IRequest<OperationResult>`
- Queries: `IRequest<T>` where T is a DTO or list

**Structure:**
```
Commands/
  Auth/        — Login, Register
  Repository/  — 40+ commands (CRUD, scan, restore, export, retention...)
  Security/    — ArtifactKey management
  Setup/       — WatchedDirectory setup

Queries/
  Repository/  — 26 queries (history, versions, diff, restore data...)
  Search/      — GlobalSearchIndex
  Security/    — ArtifactKeyRing
```

**Strengths:** Clear read/write separation. Logic is easy to locate — each operation in its own file.

**Problems:**
- `Commands/Repository/` contains 40+ files in a flat structure — no grouping by bounded context
- Some handlers mix queries with side effects (scan handler triggers a cloud push — intentional but non-obvious)

**Recommendations:**
- Split `Commands/Repository/` into subfolders: `Versioning/`, `Retention/`, `Bundle/`, `Cloud/`, `Setup/`
- Document command side effects in XML summary comments

---

### 3. Mediator Pattern (MediatR)

**Where:** All commands and queries are dispatched via `IMediator.Send()`.

**Why:** Decouples ViewModels from handlers. ViewModels do not know about concrete implementations.

**Pipeline behaviors** (`Common/Behaviors/`):
1. `ValidationBehavior` — FluentValidation runs before each handler
2. `LoggingBehavior` — structured request logging

**Strengths:** Single entry point for all use cases. Cross-cutting concerns are easy to add.

**Problems:** With 40+ commands, IntelliSense on `mediator.Send(new ...)` does not infer the result type without an explicit type annotation.

---

### 4. Repository Pattern

**Where:** `IRepositoryRepository`, `IRepositorySnapshotRepository`, `ISetupRepository`, `IUserProfileRepository`

**Why:** Abstracts data access. Application does not depend on EF Core directly.

**Implementation:** EF Core implementations in `Veyra.Infrastructure.Data/`. Test fakes in test projects.

**Problems:**
- `IRepositorySnapshotRepository` is too large (15+ methods) — ISP violation. Should be split into `ISnapshotWriteRepository`, `ISnapshotReadRepository`, `ISnapshotDiffRepository`.

---

### 5. Result Pattern

**Where:** `OperationResult<T>` and `OperationResult` in `Common/Results/`

**Why:** Explicit error handling without exceptions. Handlers return Success/Failure instead of throwing.

**Implementation:**
```csharp
return OperationResult<T>.Ok(dto);
return OperationResult<T>.Fail("message");
```

**Strengths:** ViewModels can check `result.Success` without try/catch.

**Problems:** Some query handlers return a nullable `T?` directly without a Result wrapper — inconsistency between query and command styles.

---

### 6. MVVM (Model-View-ViewModel)

**Where:** All of `Veyra.Desktop` — ViewModels inherit `ObservableObject` (CommunityToolkit.Mvvm).

**Why:** Data binding in Avalonia, testability of UI logic without rendering.

**Implementation:**
- `[ObservableProperty]` — auto-generates properties with `INotifyPropertyChanged`
- `[RelayCommand]` — async commands for UI actions
- `[NotifyPropertyChangedFor]` — computed dependent properties

**Navigation:** Via `INavigationService` (not directly).
**Dialogs:** Via `IWindowService`.
**Localization:** `Loc.T("key")` or `TrExtension` in XAML.

**Problems:**
- `AppSettingsViewModel.cs` and `RepositoryExplorerViewModel.cs` are very large (2000+ lines) — God ViewModel anti-pattern
- Constructor DI in ViewModels is correct, but some VMs receive 10+ dependencies

---

### 7. Dependency Injection

**Where:** `DependencyInjection.cs` in each layer, registered in `Veyra.Desktop/Program.cs`.

**Why:** Loosely coupled components, testability.

**Implementation:** Microsoft.Extensions.DependencyInjection via extension methods `AddApplication()`, `AddInfrastructureData()`, etc.

---

### 8. Content Addressable Storage (CAS)

**Where:** `Veyra.Infrastructure.Native/Storage/` — block-based file storage.

**Why:** Automatic block deduplication. If two files share the same block (BLAKE3 hash), only one copy is stored.

**Implementation:**
- Block address = its BLAKE3 hash (content determines address)
- Native blocks: `{root}/blocks/{h[0:2]}/{h[2:4]}/{hash}.zst` (Zstd compression)
- Managed blocks: `{root}/managed/blocks/{h[0:2]}/{h[2:4]}/{hash}.bin` (SHA-256 prefix, Brotli)

**Strengths:** Deduplication is transparent to the application layer.

---

### 9. Snapshot Pattern

**Where:** `RepositorySnapshot`, `SnapshotFileLink`, `RepositorySnapshotEntry`

**Why:** Point-in-time captures of the filesystem. Each snapshot is a complete picture at a given moment, but stores only references to blocks — no data duplication.

**Key invariants:**
- Snapshots are immutable once created
- Blocks outlive snapshots — deleted only when no live snapshot references them
- Trigger classification: `auto_snapshot_*`, `scheduled_snapshot_*`, `initial_snapshot_*` → automatic; everything else → manual/working

---

### 10. Soft Delete

**Where:** All entities have `IsDeleted`, `DeletedAt`. EF Core global query filter hides deleted rows.

**Why:** History preservation, recovery capability, safe deletion.

**Important:** The retention service performs two-phase deletion (soft-mark → `ExecuteDeleteAsync`) in a single transaction — rows are physically removed immediately.

---

### 11. Pipeline Behavior (Decorator over MediatR)

**Where:** `Common/Behaviors/` — `ValidationBehavior`, `LoggingBehavior`.

**Why:** Cross-cutting concerns applied to all handlers without modifying their code.

**Implementation:** `IPipelineBehavior<TRequest, TResponse>` — chain: Validation → Logging → Handler.

---

### 12. Strategy Pattern

**Where:**
- Sync conflict resolution: `RepositorySyncConflictStrategies` (`last_write_wins`, etc.)
- Retention storage modes: `RepositoryRetentionStorageModes`
- Trigger classification: `RepositorySnapshotTriggerClassifier`

**Why:** Behavior is selected dynamically from a string configuration key.

---

### 13. Observer / Event-Driven (partial)

**Where:** `Veyra.Domain/Observability/` — OperationJournal entries. Desktop services subscribe to filesystem events.

**Why:** Decoupled notification — scan service signals changes without direct coupling to the UI.

**Problems:** No full Domain Events mechanism — events flow through service calls, not a domain event bus.

---

### 14. Background Worker

**Where:** `Veyra.Desktop/Services/` — sync orchestrator, retention defaults applier, FS event queue.

**Why:** Scan, cloud push, retention — heavy operations run in background without blocking the UI.

**Implementation:** `IHostedService` + `CancellationToken`-based cancellation.

---

### 15. Adapter Pattern

**Where:** `Veyra.Infrastructure.Native/` — P/Invoke wrapper over the Rust DLL.

**Why:** Adapts the Rust API (unsafe C-style) to .NET interfaces (`IFileContentStore`, `IRepositoryScanner`).

---

### 16. Gateway Pattern

**Where:** `Veyra.Infrastructure.Sync/` — HTTP client for the Go cloud API.

**Why:** Isolates cloud communication behind the `ICloudSyncService` interface. Application has no knowledge of HTTP/REST.

---

## Patterns not implemented but valuable for the project

### Domain Events
**Problem:** No domain events mechanism. When a snapshot is created, the cloud push is triggered via a direct call inside `ScanRepositoryHandler`.
**Solution:** `IDomainEvent` + MediatR `INotification` + `IDomainEventPublisher`. Snapshot created → `SnapshotCreatedEvent` → `CloudPushHandler`, `RetentionCheckHandler` react independently.

### Unit of Work
**Problem:** The EF DbContext is managed via DI scope, but there is no explicit UoW interface. Some handlers perform multiple DbContext operations without an explicit transaction.
**Solution:** `IUnitOfWork` with an explicit `CommitAsync()`.

### Specification Pattern
**Problem:** The retention service contains complex snapshot filtering logic (by tags, triggers, dates) embedded directly in the service. Individual rules are hard to test.
**Solution:** `ISnapshotRetentionSpecification` — each deletion rule (`NeverDeleteTagSpec`, `MaxSnapshotsSpec`, `MaxAgeSpec`) as a separate testable specification.

### Retry Policy / Resilience
**Problem:** Cloud sync HTTP calls have no retry/circuit-breaker. Transient failures are not handled gracefully.
**Solution:** Polly — `RetryPolicy` for upload/download, `CircuitBreakerPolicy` for connectivity failures.

### Aggregate Root
**Problem:** `Repository` is the aggregate root, but this is not expressed explicitly — there is no `AggregateRoot<TId>` base class, no protected invariants.
**Solution:** `abstract class AggregateRoot<TId>` with methods for accumulating domain events.

---

## Summary table

| Pattern | Status | Improvement priority |
|---------|--------|----------------------|
| Clean Architecture | ✅ In use | Low — Desktop→Infrastructure dependency |
| CQRS / MediatR | ✅ In use | Medium — Commands/Repository grouping |
| Repository | ✅ In use | Medium — ISP violation in SnapshotRepository |
| Result Pattern | ✅ In use | Low — inconsistency queries vs commands |
| MVVM | ✅ In use | Medium — God ViewModel refactoring |
| CAS / Deduplication | ✅ In use | Low |
| Snapshot Pattern | ✅ In use | Low |
| Soft Delete | ✅ In use | Low |
| Pipeline Behaviors | ✅ In use | Low |
| Strategy | ✅ In use | Low |
| Background Worker | ✅ In use | Low |
| Adapter (Rust) | ✅ In use | Low |
| Gateway (Cloud) | ✅ In use | Low |
| Domain Events | ❌ Missing | High — decoupling snapshot→cloud |
| Unit of Work | ❌ Missing | Medium |
| Specification | ❌ Missing | High — retention rules |
| Retry/Resilience | ❌ Missing | High — cloud reliability |
| Aggregate Root | ❌ Missing | Medium |
