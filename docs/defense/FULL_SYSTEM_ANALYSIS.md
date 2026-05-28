# Полный технический анализ VeyraFlow

## Как проводился аудит

Анализ выполнен по реальному коду solution `VeyraFlow.sln`, через поиск классов, интерфейсов, handler-ов, `ViewModel`, сущностей, таблиц, P/Invoke entrypoint-ов, Rust-модулей, Go endpoint-ов и тестов.  
Существующие `docs/*.md` и `.claude/docs/*.md` были просмотрены только как вторичный источник и не использовались как единственное основание для выводов.

Важное позиционирование проекта:

- Корректная формулировка: desktop-приложение для отслеживания состояния выбранных файлов и папок, создания истории версий, снимков состояния, сравнения версий и восстановления файлов.
- Некорректная формулировка: полноценный файловый проводник или замена Windows Explorer.
- В коде не найдено подтверждения, что приложение позиционируется как классический explorer с обещанием копирования, перемещения, drag-and-drop управления файлами и полного файлового администрирования.

## Critical findings

1. Презентация и титульные формулировки используют термин `автономный файловый менеджер`, который искажает реальное назначение продукта. По коду проект ближе к `versioned file-state tracker / snapshot-based desktop app`.

2. Полное восстановление истории из облака не реализовано.  
   В `src/Veyra.Infrastructure.Data/Sync/RepositoryCloudSyncOrchestrator.cs` метод `RestoreRepositoryFromCloudAsync(..., restoreFullHistory: true)` явно пишет предупреждение, что full history restore пока не реализован, и фактически работает только путь восстановления последнего snapshot.

3. В презентации заявлен `gRPC`, но основная рабочая облачная реализация в коде — REST.  
   Реальные endpoint-ы зарегистрированы в `server/cloud-api/cmd/api/main.go`.  
   `proto/sync.proto` и `tools/sync-agent/cmd/sync-agent/main.go` существуют, но gRPC-агент выглядит как отдельный прототип, не встроенный в основной cloud sync pipeline.

4. Пакет `Dapper` подключен в `src/Veyra.Infrastructure.Data/Veyra.Infrastructure.Data.csproj`, но в inspected source не найдено реального использования `using Dapper`, `QueryAsync`, `DynamicParameters` и т.п.  
   Практически все оптимизации чтения выполнены через EF Core + raw `DbConnection/CreateCommand` в `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`.

5. Проверка целостности локальных block-файлов несимметрична.  
   `src/Veyra.Infrastructure.Data/Setup/Integrity/EfRepositoryIntegrityService.cs` для managed-блоков с префиксом `sha256-` пересчитывает SHA-256 содержимого, а для native block hash путь лишь резолвится и проверяется на существование. Отдельной повторной верификации BLAKE3 payload по локальному файлу в этой службе не найдено.

6. Локальные cloud access/refresh token-ы сохраняются в SQLite-профиле пользователя.  
   Это видно в `src/Veyra.Infrastructure.Data/Auth/UserProfiles/EfUserProfileRepository.cs` и в сущности `src/Veyra.Domain/Entities/UserProfile.cs`. Для production это слабее, чем выделенное secure storage.

7. На не-Windows платформах часть локальной защиты уходит в plaintext fallback.  
   Это относится как минимум к `src/Veyra.Desktop/Services/Auth/LocalCredentialStore.cs` и master key storage в native/security слое.

## 1. Карта solution и монорепозитория

### Верхний уровень

- `VeyraFlow.sln`  
  Главная solution, объединяющая desktop, application, domain, infrastructure, tests.

- `src/`  
  Основные .NET-проекты.

- `native/veyra_core`  
  Rust cdylib, вызываемая из .NET через P/Invoke.

- `server/cloud-api`  
  Go REST API для auth, sync metadata и block storage.

- `proto/`  
  Proto-файлы, связанные с отдельным sync-agent prototype.

- `tests/`  
  xUnit-проекты для Application, Infrastructure.Data, Desktop, Domain.

- `docs/`, `.claude/docs/`  
  Существующая документация и внутренние заметки.

- `tools/`  
  Вспомогательные утилиты, включая `tools/NativeSmokeCheck` и `tools/sync-agent`.

### Проекты solution

| Проект | Назначение | Ключевые файлы и классы | Зависимости | Интерфейсы / адаптеры | Входные точки |
|---|---|---|---|---|---|
| `src/Veyra.Desktop` | Presentation layer на Avalonia, MVVM, навигация, окна, background UI services | `App.axaml.cs`, `Program.cs`, `ViewLocator.cs`, `ViewModels/Windows/Shell/MainWindowViewModel.cs`, `ViewModels/Pages/Explorer/RepositoryExplorerViewModel.cs`, `ViewModels/Pages/Settings/AppSettingsViewModel.cs` | `Veyra.Application`, infrastructure через DI | использует `IMediator`, `IWindowService`, `IConnectivityStatusService`, `IRepositoryCloudSyncOrchestrator` | `Program.cs`, `App.axaml.cs` |
| `src/Veyra.Application` | CQRS/MediatR, use cases, DTO, pipeline behaviors, abstractions | `DependencyInjection.cs`, `Commands/*`, `Queries/*`, `Common/Behaviors/*`, `Services/Diff/WordSemanticProjection.cs` | `Veyra.Domain` | объявляет `IRepositoryScanner`, `IFileContentStore`, `IRepositorySnapshotRepository`, `IAuthService`, `ICloudSyncService` и др. | `DependencyInjection.cs` |
| `src/Veyra.Domain` | Чистые сущности, value-like DTO-ish domain objects, наблюдаемость | `Entities/Repository.cs`, `RepositorySnapshot.cs`, `FileIdentity.cs`, `FileVersion.cs`, `FileVersionBlock.cs`, `UserProfile.cs`, `Watched/*` | без зависимостей на infrastructure | сущности, перечисления, domain contracts | нет runtime entrypoint |
| `src/Veyra.Infrastructure.Data` | SQLite persistence, EF Core mapping, snapshot repository, retention, recovery, archive, cloud orchestration | `Persistence/Context/VeyraDbContext.cs`, `Setup/Snapshots/EfRepositorySnapshotRepository.cs`, `Setup/EfRepositoryRepository.cs`, `Setup/EfSetupRepository.cs`, `Setup/Retention/EfRepositoryRetentionService.cs`, `Setup/Integrity/EfRepositoryIntegrityService.cs`, `Setup/Recovery/EfRepositoryRecoveryService.cs`, `Sync/RepositoryCloudSyncOrchestrator.cs` | `Application`, `Domain` | реализует `IRepositoryRepository`, `ISetupRepository`, `IRepositorySnapshotRepository`, `IRepositoryRetentionService`, `IRepositoryRecoveryService`, `IRepositoryIntegrityService` | `DependencyInjection*.cs` |
| `src/Veyra.Infrastructure.Native` | P/Invoke adapter к Rust, local encryption/key management, native diff/store/scan engines | `Interop/VeyraCoreNative.cs`, `Scanning/RustRepositoryScanner.cs`, `Storage/RustFileContentStore.cs`, `Runtime/NativeRuntimeHealthService.cs`, `Security/*` | `Application`, `Domain` | реализует `IRepositoryScanner`, `IFileContentStore`, `ITextDiffEngine`, `ISnapshotComparisonEngine`, `IArtifactKeyManagementService` | `DependencyInjection*.cs` |
| `src/Veyra.Infrastructure.Sync` | HTTP clients к cloud API, token policy, Polly retry, no-op cloud adapters | `Auth/AuthHttpService.cs`, `Auth/AccessTokenPolicyService.cs`, `Sync/Cloud/CloudSyncHttpService.cs`, `Sync/Cloud/NoopRepositoryCloudSyncOrchestrator.cs` | `Application`, `Domain` | реализует `IAuthService`, `ICloudSyncService`, `IAccessTokenPolicyService` | `DependencyInjection*.cs` |
| `src/Veyra.Shared.Logging` | Общая обвязка логирования | проект-обертка для logging setup | base .NET logging packages | logging helpers | через DI/Serilog wiring |

### Тестовые проекты

| Проект | Что проверяет |
|---|---|
| `tests/Veyra.Application.Tests` | handler-ы restore/scan, Word semantic projection |
| `tests/Veyra.Infrastructure.Data.Tests` | retention, recovery, bundle export/import, cloud management, SVG structural diff, audio diff |
| `tests/Veyra.Desktop.Tests` | image diff pipeline, filesystem queue, localization, архитектурные ограничения desktop layer |
| `tests/Veyra.Domain.Tests` | project присутствует в solution, но основная глубина тестов сосредоточена в application/data/desktop |

### Дополнительные инструменты вне основного списка пользователя

- `tools/NativeSmokeCheck/Program.cs`  
  Проверка, что native runtime поднимается и экспортирует ожидаемые entrypoint-ы.

- `tools/sync-agent/cmd/sync-agent/main.go`  
  Отдельный gRPC prototype. Не найдено подтверждения, что он является частью production cloud sync path.

## 2. Архитектура по слоям

### Presentation layer

Расположен в `src/Veyra.Desktop`.

Реальные признаки:

- Avalonia App startup: `src/Veyra.Desktop/Program.cs`, `src/Veyra.Desktop/App.axaml.cs`
- MVVM views/viewmodels: `src/Veyra.Desktop/Views/*`, `src/Veyra.Desktop/ViewModels/*`
- динамическое сопоставление `ViewModel -> View`: `src/Veyra.Desktop/ViewLocator.cs`
- навигация между страницами: `src/Veyra.Desktop/Services/Navigation/NavigationService.cs`, `WindowService.cs`

Presentation layer не должен напрямую:

- читать/писать SQLite;
- вызывать Rust FFI;
- знать про `HttpClient` cloud API;
- манипулировать таблицами или block storage.

Вместо этого он вызывает `IMediator`, UI services и orchestrator-интерфейсы.

### Application layer

Расположен в `src/Veyra.Application`.

Это слой use case-ов и orchestration без UI и без конкретной persistence/native/HTTP реализации.

Примеры:

- создание репозитория: `Commands/Repository/CreateRepositoryWithFormatsHandler.cs`
- snapshot/scan запуск: `Commands/Repository/ScanRepositoryHandler.cs`
- restore: `Commands/Repository/RestoreFileVersionHandler.cs`
- text diff: `Queries/Repository/GetTextDiffHandler.cs`
- global search index: `Queries/Search/GetGlobalSearchIndexHandler.cs`

### Domain layer

Расположен в `src/Veyra.Domain`.

Это model layer, который не должен знать:

- о `DbContext`;
- о `HttpClient`;
- о `DllImport`;
- об Avalonia.

Основные сущности:

- `Repository`
- `RepositorySnapshot`
- `RepositorySnapshotEntry`
- `SnapshotFileLink`
- `FileIdentity`
- `FileVersion`
- `FileVersionBlock`
- `FileVersionTextDiff`, `FileVersionTextDiffHunk`, `FileVersionTextDiffLine`, `TextLineAtom`
- `RepositorySyncQueueItem`
- `UserProfile`
- `WatchedDirectory`, `D_WatchedFormat`, `WatchedDirectoryFormat`
- `OperationJournalEntry`

### Infrastructure layer

Разбита минимум на три подпакета:

- `src/Veyra.Infrastructure.Data`  
  SQLite, EF Core, snapshot graph, retention, recovery, local metadata.

- `src/Veyra.Infrastructure.Native`  
  Rust adapters, storage, scanning, native compare/diff, encryption и local key management.

- `src/Veyra.Infrastructure.Sync`  
  Cloud REST clients, auth, token refresh policy, retry, no-op fallback adapters.

### Native layer

Расположен в `native/veyra_core`.

Граница C# -> Rust проходит через `src/Veyra.Infrastructure.Native/Interop/VeyraCoreNative.cs`.

Подтвержденные P/Invoke entrypoint-ы:

- `veyra_scan_directory_utf8`
- `veyra_scan_directory_limited_utf8`
- `veyra_scan_directory_limited_v2_utf8`
- `veyra_store_file_blocks_utf8`
- `veyra_restore_file_blocks_utf8`
- `veyra_build_text_diff_utf8`
- `veyra_render_image_diff_utf8`
- `veyra_compare_snapshot_links_utf8`
- `veyra_compare_repository_paths_utf8`
- `veyra_plan_repository_versions_utf8`
- `veyra_last_error_utf8`

Соответствующие Rust-модули:

- `native/veyra_core/src/scan/mod.rs`
- `native/veyra_core/src/block_store/mod.rs`
- `native/veyra_core/src/text_diff/mod.rs`
- `native/veyra_core/src/image_diff/mod.rs`
- `native/veyra_core/src/snapshot_compare/mod.rs`
- FFI bridge: `native/veyra_core/src/ffi/api.rs`

### Cloud layer

Расположен в `server/cloud-api`.

Реальная рабочая граница между desktop и cloud — REST.

Подтвержденные endpoint-ы в `server/cloud-api/cmd/api/main.go`:

- `GET /healthz`
- `POST /api/register`
- `POST /api/login`
- `POST /api/refresh`
- `POST /api/logout`
- `GET /api/sync/repositories`
- `DELETE /api/sync/repositories/{repositoryId}`
- `GET /api/sync/repositories/{repositoryId}/latest`
- `POST /api/sync/repositories/{repositoryId}/snapshots`
- `HEAD /api/sync/blocks/{blockHash}`
- `POST /api/sync/blocks/batch`
- `POST /api/sync/blocks/{blockHash}`
- `GET /api/sync/blocks/{blockHash}`
- `GET /api/admin/storage/metrics`
- `POST /api/admin/storage/repair`

### Направление зависимостей

Корректное направление:

- `Desktop -> Application`
- `Infrastructure.* -> Application + Domain`
- `Application -> Domain`
- `Domain -> никуда вниз`

Почему это важно:

- Domain не должен знать про Infrastructure, иначе snapshot/restore model начнет зависеть от SQLite/HTTP/FFI.
- UI не должен напрямую работать с SQLite, Rust и Go API, иначе сломается testability, offline policy и подмена адаптеров.
- concrete adapters живут в infrastructure, abstractions — в application.

### Composition root и DI

Ключевые DI-файлы:

- `src/Veyra.Application/DependencyInjection.cs`
- `src/Veyra.Infrastructure.Data/DependencyInjection*.cs`
- `src/Veyra.Infrastructure.Native/DependencyInjection*.cs`
- `src/Veyra.Infrastructure.Sync/DependencyInjection*.cs`
- `src/Veyra.Desktop/CompositionRoot/DependencyInjection*.cs`

Сборка контейнера:

1. `src/Veyra.Desktop/App.axaml.cs` поднимает service provider.
2. Desktop composition root регистрирует `AddApplication()`.
3. Затем подключаются sync/data/native инфраструктурные слои.
4. Затем desktop-переопределения вроде `RepositoryFsEventQueueService`.

Практически важно:

- в `src/Veyra.Infrastructure.Sync` регистрируются no-op cloud adapters;
- затем `src/Veyra.Infrastructure.Data` регистрирует реальный `RepositoryCloudSyncOrchestrator`;
- итоговая рабочая реализация зависит от порядка регистрации.

Это рабочее решение, но архитектурно хрупкое место.

## 3. Паттерны, реально найденные в коде

| Паттерн | Где найден | Зачем нужен в проекте | Что было бы хуже без него | Ограничения |
|---|---|---|---|---|
| MVVM | `src/Veyra.Desktop/ViewModels/*`, `Views/*` | отделяет UI от use case-ов | code-behind разрастался бы и смешивал UI с snapshot/sync logic | часть ViewModel очень крупные, особенно `AppSettingsViewModel` и `RepositoryExplorerViewModel` |
| CQRS | `src/Veyra.Application/Commands/*`, `Queries/*` | разделяет операции изменения состояния и чтения | пришлось бы строить “богатые сервисы” с трудно тестируемым API | read/write boundary местами все еще проходит через один repository interface |
| Mediator / MediatR | `src/Veyra.Application/DependencyInjection.cs`, handler-ы | UI не зависит от concrete services напрямую | связность между окнами и бизнес-логикой была бы выше | часть сценариев уже очень насыщена handler orchestration |
| Repository pattern | `IRepositoryRepository`, `IRepositorySnapshotRepository`, `ISetupRepository` | инвертирует доступ к данным | UI и application пришлось бы знать про EF Core | `IRepositorySnapshotRepository` получился очень крупным интерфейсом |
| Unit of Work через `DbContext` | `src/Veyra.Infrastructure.Data/Persistence/Context/VeyraDbContext.cs` | консистентные транзакции snapshot/relink/retention | возрастет риск частично сохраненных графов | большие транзакции snapshot/retention могут быть тяжёлыми |
| DI | все `DependencyInjection*.cs` | подмена сканера, content store, cloud sync, no-op сервисов | сложно тестировать offline/native/cloud fallback | есть чувствительность к registration order |
| Adapter / Gateway к Rust | `src/Veyra.Infrastructure.Native/Interop/VeyraCoreNative.cs`, `RustRepositoryScanner`, `RustFileContentStore` | изолирует FFI от application layer | FFI попадет в handler-ы и ViewModel | сложнее диагностировать несовпадение native entrypoint-ов |
| Service layer | `EfRepositoryRetentionService`, `EfRepositoryRecoveryService`, `CloudRepositoryManagementService` | инкапсулирует сложные orchestration use case-ы | логика cleanup/recovery/cloud расползлась бы по UI | сервисы местами очень большие |
| DTO mapping | весь `src/Veyra.Application/DTOs`, плюс AutoMapper registration | отделяет EF/entities от UI/query responses | UI начал бы знать внутреннюю модель и soft-delete поля | AutoMapper заявлен, но часть mapping делается вручную |
| Validation pipeline | `ValidationBehavior.cs` + FluentValidation registration | единая точка валидации command/query | валидация размазалась бы по handler-ам | не все guard-ы только через FluentValidation, часть логики в handler-ах |
| Logging pipeline | `LoggingBehavior.cs` | единое измерение latency и ошибок handler-ов | сложнее трассировать цепочки | не покрывает прямые infrastructure вызовы вне MediatR |
| Operation journal pipeline | `OperationJournalBehavior.cs` | журналирует команды в SQLite | диагностика restore/sync/retention была бы слабее | `ScanRepositoryCommand` сознательно исключен из journal |
| Result pattern | `OperationResult`, `OperationResult<T>` | унифицирует ошибки для UI | больше исключений и меньше user-facing сообщений | часть инфраструктурных методов всё ещё бросает исключения |
| Strategy | conflict strategy в `RepositorySyncConflictStrategies`, retention filters, image diff modes | переключаемое поведение sync/retention/preview | if/else рос бы в UI и orchestrator | стратегии пока выражены строками/enum-like constants |
| Observer / event handling | `FileSystemWatcher` в `RepositoryExplorerViewModel`, connectivity/status events | live update UI и queued watcher sync | пришлось бы делать только периодический polling | FS watcher требует debounce и fallback к full scan |
| Queue pattern | `RepositorySyncQueueItem`, `RepositoryFsEventQueueService` | offline sync и live FS delta processing | потеря событий и плохой retry | queue state machine уже сложная |
| Offline-first | `App.axaml.cs`, connectivity services, queue/orchestrator | локальная работа без облака | app становился бы зависим от сети | часть cloud UX всё еще сложная для объяснения |
| Soft delete | query filters в `VeyraDbContext.cs` | безопасный cleanup и retention staging | hard-delete везде усложнил бы recovery | retention в итоге часто доводит soft-delete до hard-delete sweep |
| Retry / Polly | `src/Veyra.Infrastructure.Sync/DependencyInjection*.cs`, scheduler retry | повышает устойчивость auth/cloud | больше transient failures в UI | не все цепочки одинаково защищены retry |

## 4. Полный жизненный цикл репозитория

### Выбор директории и форматов

UI:

- `src/Veyra.Desktop/ViewModels/Windows/CreateRepository/CreateRepositoryWindowViewModel.cs`
- `src/Veyra.Desktop/ViewModels/Pages/SetupWizard/SetupWizardViewModel.cs`

Команды:

- `CreateRepositoryWithFormatsCommand`
- `SaveInitialSetupCommand`
- `AddDirectoryAndCreateRepositoryCommand`
- `EnsureRepositoriesCommand`

### Сохранение initial setup

`src/Veyra.Application/Commands/Setup/SaveInitialSetupHandler.cs`:

1. валидирует и сохраняет watched directories и tracked extensions через `ISetupRepository`;
2. вызывает `IRepositoryRepository.EnsureRepositoriesForAllDirectoriesAsync`;
3. запускает initial scan для выбранных репозиториев;
4. после этого вызывает `INativeSetupApplier.ApplySetupAsync`.

Persistence:

- `src/Veyra.Infrastructure.Data/Setup/EfSetupRepository.cs`

Что делает `EfSetupRepository`:

- `ReplaceWatchedDirectoriesAsync`
- `ReplaceTrackedExtensionsAsync`
- `LinkAllActiveDirectoriesToAllActiveFormatsAsync`
- soft-delete старых связей вместо немедленного физического удаления

### Создание repository record

`src/Veyra.Infrastructure.Data/Setup/EfRepositoryRepository.cs`:

- создает `Repository`, если директория еще не связана;
- либо реактивирует soft-deleted repository;
- хранит имя, описание, путь, retention/sync defaults, cloud flags.

### Первичное сканирование

`src/Veyra.Application/Commands/Repository/CreateRepositoryWithFormatsHandler.cs`:

1. нормализует путь;
2. добавляет directory в setup;
3. гарантирует наличие tracked extensions;
4. линкует directory <-> formats;
5. запускает `InitialSnapshotCreation.RunWithBoundedRetryAsync(...)`;
6. применяет native setup;
7. возвращает `RepositoryCreationOutcomeDto`.

### Отображение в UI

После initial setup `MainWindowViewModel` грузит dashboard, а `RepositoryDashboardViewModel` получает список через `GetAllRepositoriesQuery`.

### Запуск background monitoring

После deferred startup:

- `App.axaml.cs` вызывает `_snapshotScheduler.Start()`;
- `RepositoryExplorerViewModel` при открытии репозитория поднимает `FileSystemWatcher`;
- события watcher уходят в `RepositoryFsEventQueueService`, а не сразу в SQLite.

## 5. Создание snapshot

### Ручной snapshot

UI flow:

1. `RepositoryExplorerViewModel.CreateSnapshotAsync()`
2. `SnapshotNameDialogWindowViewModel`
3. `GetPendingFileDiffPreviewQuery` для показа preview измененных файлов
4. `ScanRepositoryCommand` с `SaveFileVersions: true`, `TriggerOverride: "manual_snapshot"`

### Handler и scanner

- `src/Veyra.Application/Commands/Repository/ScanRepositoryHandler.cs`
- `src/Veyra.Infrastructure.Native/Scanning/RustRepositoryScanner.cs`

Что делает scanner:

- предотвращает параллельные scan одного repository;
- пытается вызвать native scan через `VeyraCoreNative.ScanDirectoryJson(...)`;
- при недоступности native runtime использует managed fallback;
- фильтрует файлы по tracked extensions и exclusion rules;
- считает SHA-256 файлов для managed path.

### Сохранение snapshot graph

Основной метод:

- `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`
- `SaveSnapshotAsync(...)`

Ключевые действия:

1. сравнение текущего состояния с предыдущим snapshot;
2. создание `RepositorySnapshot`;
3. запись `RepositorySnapshotEntry`;
4. создание/поиск `FileIdentity`;
5. сравнение с предыдущими `FileVersion`;
6. вызов `IFileContentStore.StoreFileAsync(...)`, если нужен новый version;
7. запись `FileVersionBlock`;
8. создание `SnapshotFileLink`;
9. кэширование text diff для supported text formats.

Если изменений нет:

- snapshot shell может быть удален;
- возвращается `NoChangesDetected`.

Если файл занят:

- `TryStoreBlocksAsync` переводит ситуацию в busy/unavailable warning, а не в фатальное падение всей операции.

## 6. UI-карта

| View / окно | ViewModel | Основные Application command/query |
|---|---|---|
| `Views/MainWindow.axaml` | `MainWindowViewModel` | координирует страницы, сам напрямую use case не содержит |
| `Views/Pages/Dashboard/RepositoryDashboardView.axaml` | `RepositoryDashboardViewModel` | `EnsureRepositoriesCommand`, `GetAllRepositoriesQuery` |
| `Views/Pages/Explorer/RepositoryExplorerView.axaml` | `RepositoryExplorerViewModel` | `ScanRepositoryCommand`, `GetRepositoryDetailQuery`, `GetPendingChangesQuery`, `GetSnapshotHistoryQuery`, `RestoreFileVersionCommand`, `GetTextDiffQuery` |
| `Views/Pages/RepositorySettings/RepositorySettingsView.axaml` | `RepositorySettingsViewModel` | `GetRepositoryDetailQuery`, `GetTrackedExtensionsQuery`, `UpdateRepositoryConfigurationCommand`, `RunRepositoryRetentionCommand`, `DeleteRepositoryCommand` |
| `Views/Pages/Search/GlobalSearchView.axaml` | `GlobalSearchViewModel` | `GetGlobalSearchIndexQuery`, `GetRepositorySnapshotChangedFilesQuery` |
| `Views/Pages/Settings/AppSettingsView.axaml` | `AppSettingsViewModel` | `GetAllRepositoriesQuery`, `GetRepositoryDetailQuery`, `UpdateRepositoryConfigurationCommand`, `RotateArtifactKeyCommand`, auth/cloud actions |
| `Views/Pages/SetupWizard/*` | `SetupWizardViewModel` | `SaveInitialSetupCommand`, `GetAllRepositoriesQuery` |
| `Views/Windows/CreateRepositoryWindow.axaml` | `CreateRepositoryWindowViewModel` | `CreateRepositoryWithFormatsCommand`, `ScanRepositoryCommand` |
| `Views/Windows/FileVersionCompareWindow.axaml` | `FileVersionCompareWindowViewModel` | `GetTextDiffQuery`, `GetFileVersionDiffPreviewQuery`, `GetFileVersionTextContentQuery` |
| `Views/Windows/CloudRepositoryManagerWindow.axaml` | `CloudRepositoryManagerWindowViewModel` | repository management поверх `CloudRepositoryManagementService` |

## 7. База данных SQLite

### `VeyraDbContext`

Файл:

- `src/Veyra.Infrastructure.Data/Persistence/Context/VeyraDbContext.cs`

Основные `DbSet`:

- `Repositories`
- `RepositorySnapshots`
- `RepositorySnapshotEntries`
- `SnapshotFileLinks`
- `FileIdentities`
- `FileVersions`
- `FileVersionBlocks`
- `FileVersionTextDiffs`
- `FileVersionTextDiffHunks`
- `FileVersionTextDiffLines`
- `TextLineAtoms`
- `RepositorySyncQueueItems`
- `WatchedDirectories`
- `WatchedFormats`
- `WatchedDirectoryFormats`
- `UserProfiles`
- `OperationJournalEntries`

### Ключевые ограничения и индексы

- `FileIdentity`: unique `(RepositoryId, RelativePath)`
- `SnapshotFileLink`: unique `(SnapshotId, FileIdentityId)`
- `FileVersionBlock`: unique `(FileVersionId, Sequence)`, index по `BlockStorageKey`
- `RepositorySnapshotEntry`: unique `(SnapshotId, RelativePath)`
- `RepositorySyncQueueItem`: unique `(RepositoryId, SnapshotId, OperationType)`
- `FileVersionTextDiff`: unique `(LeftFileVersionId, RightFileVersionId, MaxLines)`
- `RepositorySnapshot`: индекс `(RepositoryId, IsArchived, CreatedAt)`
- `OperationJournalEntries`: индексы по времени, категории, repository

### Soft delete

Во многих сущностях включены глобальные query filter-ы `!IsDeleted`.  
Это важно для snapshot history, retention, relink и recovery logic.

### EF Core vs raw SQL

Подтверждено:

- основная persistence-модель — EF Core;
- для тяжелых batch/read path используются raw `DbConnection/CreateCommand` в `EfRepositorySnapshotRepository.cs`;
- реального использования Dapper в inspected source не найдено, несмотря на пакет в `.csproj`.

## 8. Background services

### Deferred startup

Файл:

- `src/Veyra.Desktop/App.axaml.cs`

Что делает:

- показывает shell рано;
- в фоне инициализирует БД;
- проверяет native runtime health;
- понимает, есть ли активный cloud profile;
- при необходимости запускает cloud restore и resume sync queue;
- выполняет startup health-check recovery;
- стартует snapshot scheduler.

### Snapshot scheduler

Файл:

- `src/Veyra.Desktop/Services/Scheduling/SnapshotSchedulerService.cs`

Что делает:

- периодически ищет repositories, где `LastScannedAt` старше заданного интервала;
- запускает scheduled scan с retry;
- запускает due retention;
- запускает due integrity verification;
- после этого прокачивает cloud sync queue.

### Live filesystem monitoring

Файлы:

- `src/Veyra.Desktop/ViewModels/Pages/Explorer/RepositoryExplorerViewModel.cs`
- `src/Veyra.Desktop/Services/Sync/FileSystemQueue/RepositoryFsEventQueueService.cs`
- `src/Veyra.Desktop/Services/Repositories/RepositoryLiveSyncDeltaBuilder.cs`

Поведение:

- watcher-события попадают в JSON queue;
- stale running items можно повторно lease-ить;
- при сложных случаях live delta builder запрашивает fallback на full scan.

### Integrity / recovery / retention

- `EfRepositoryIntegrityService`
- `EfRepositoryRecoveryService`
- `EfRepositoryRetentionService`
- `RepositorySnapshotArchiveService`

Это отдельные maintenance services, а не код внутри UI.

## 9. Тестирование

### Что найдено

- restore integrity: `tests/Veyra.Application.Tests/RestoreIntegrityTests.cs`
- cloud push gating при scan: `tests/Veyra.Application.Tests/ScanRepositoryHandlerTests.cs`
- Word semantic projection: `tests/Veyra.Application.Tests/WordSemanticProjectionTests.cs`
- retention quality gates: `tests/Veyra.Infrastructure.Data.Tests/RetentionPolicyQualityGateTests.cs`
- recovery quality gates: `tests/Veyra.Infrastructure.Data.Tests/RepositoryRecoveryQualityGateTests.cs`
- bundle round-trip: `tests/Veyra.Infrastructure.Data.Tests/RepositoryBundleQualityGateTests.cs`
- cloud repository manager: `tests/Veyra.Infrastructure.Data.Tests/CloudRepositoryManagementServiceTests.cs`
- SVG structural diff: `tests/Veyra.Infrastructure.Data.Tests/SvgStructuralDiffAnalyzerTests.cs`
- audio diff preview: `tests/Veyra.Infrastructure.Data.Tests/AudioDiffPreviewBuilderTests.cs`
- FS queue replay/dedup: `tests/Veyra.Desktop.Tests/RepositoryFsEventQueueServiceTests.cs`
- image diff pipeline: `tests/Veyra.Desktop.Tests/ImageDiffPipelineTests.cs`
- architecture rule: `tests/Veyra.Desktop.Tests/ArchitectureDependencyRulesTests.cs`

### Что покрыто хорошо

- retention safety;
- restore hash validation;
- bundle export/import;
- image/audio preview building;
- live queue replay;
- часть cloud restore planning.

### Основные пробелы

- нет найденных Go `_test.go` для `server/cloud-api`;
- нет end-to-end test, который поднимает desktop + Rust + cloud-api вместе;
- нет отдельного теста на corruption detection для native BLAKE3 block файла на локальном диске;
- нет теста полного round-trip для encrypted managed blocks через rotate/revoke key lifecycle;
- нет сильного нагрузочного теста на global search index при больших snapshot history.

## 10. Что подтверждено документацией, но перепроверено кодом

В проекте уже есть много технических заметок:

- `docs/SNAPSHOT_PIPELINE.md`
- `docs/BLOCK_STORAGE.md`
- `docs/CLOUD_ARCHITECTURE.md`
- `docs/OFFLINE_FIRST_ARCHITECTURE.md`
- `docs/retention-policies.md`
- `.claude/docs/architecture.md`
- `.claude/docs/offline.md`

Но итоговые выводы в defense-пакете основаны на коде:

- `src/*`
- `native/veyra_core/*`
- `server/cloud-api/*`
- `tests/*`
- а также содержимом `Prezentare_Ev2_Vistovschii_Serghei.pptx`.

## Итог

По коду VeyraFlow — это layered desktop system с:

- Avalonia MVVM presentation;
- MediatR/CQRS application layer;
- EF Core SQLite metadata storage;
- Rust native core для scan/storage/diff performance path;
- Go REST cloud backend;
- offline-first queue-based sync;
- snapshot/version/recovery model, а не классический file explorer.

Самые важные ограничения для защиты:

- не заявлять проект как замену Windows Explorer;
- не заявлять gRPC как основную готовую cloud transport-реализацию;
- не заявлять full-history cloud restore как finished;
- аккуратно формулировать Word semantic diff и audio diff как частично специализированные режимы, а не как универсальный анализ любых бинарных форматов.
