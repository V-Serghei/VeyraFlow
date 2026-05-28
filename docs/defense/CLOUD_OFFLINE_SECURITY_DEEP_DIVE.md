# Cloud sync, offline-first и security: deep dive

## Основание для выводов

Ключевые файлы проверки:

- `src/Veyra.Infrastructure.Sync/Auth/AuthHttpService.cs`
- `src/Veyra.Infrastructure.Sync/Auth/AccessTokenPolicyService.cs`
- `src/Veyra.Infrastructure.Sync/Sync/Cloud/CloudSyncHttpService.cs`
- `src/Veyra.Infrastructure.Sync/DependencyInjection.cs`
- `src/Veyra.Infrastructure.Sync/DependencyInjection.Auth.cs`
- `src/Veyra.Infrastructure.Sync/DependencyInjection.Cloud.cs`
- `src/Veyra.Infrastructure.Data/Sync/RepositoryCloudSyncOrchestrator.cs`
- `src/Veyra.Infrastructure.Data/Sync/CloudRepositoryManagementService.cs`
- `src/Veyra.Infrastructure.Data/Sync/CloudRepositoryOperationTracker.cs`
- `src/Veyra.Desktop/App.axaml.cs`
- `src/Veyra.Desktop/Services/Connectivity/ConnectivityStatusService.cs`
- `src/Veyra.Desktop/Services/Connectivity/CloudAvailabilityService.cs`
- `src/Veyra.Desktop/Services/Sync/Runtime/CloudSyncRuntimeControlService.cs`
- `src/Veyra.Desktop/Services/Auth/LocalCredentialStore.cs`
- `src/Veyra.Infrastructure.Native/Security/MetadataProtection/CloudMetadataProtectionService.cs`
- `src/Veyra.Infrastructure.Native/Security/KeyManagement/ArtifactMasterKeyStore.cs`
- `server/cloud-api/cmd/api/main.go`
- `server/cloud-api/internal/handlers/repos.go`
- `server/cloud-api/internal/handlers/sync.go`
- `server/cloud-api/internal/handlers/pack_storage.go`
- `server/cloud-api/internal/handlers/storage_admin.go`
- `server/cloud-api/migrations/0001_init.sql`
- `proto/sync.proto`
- `tools/sync-agent/cmd/sync-agent/main.go`

## 1. Реальная архитектура облачной части

По inspected коду облачная часть VeyraFlow построена так:

- desktop client на C# работает через REST;
- cloud API реализован на Go;
- cloud metadata хранится в PostgreSQL;
- cloud blocks хранятся в файловом storage / pack storage;
- локальный sync orchestration живет не в UI, а в `RepositoryCloudSyncOrchestrator`.

Корректная формулировка для защиты:

- `основная реализация синхронизации в текущей версии — REST API`

Некорректная формулировка:

- `проект уже полноценно использует gRPC`

## 2. Где находится client-side sync logic

### 2.1 HTTP auth

- `src/Veyra.Infrastructure.Sync/Auth/AuthHttpService.cs`
- `src/Veyra.Infrastructure.Sync/Auth/AccessTokenPolicyService.cs`

`AuthHttpService` отвечает за:

- register
- login
- refresh
- logout

`AccessTokenPolicyService` оценивает:

- валиден ли access token;
- нужно ли обновление;
- истек ли refresh token.

### 2.2 HTTP sync

- `src/Veyra.Infrastructure.Sync/Sync/Cloud/CloudSyncHttpService.cs`

Этот сервис реализует REST-вызовы:

- push snapshot metadata;
- проверить наличие блока в облаке;
- batch upload blocks;
- upload одного блока;
- download блока;
- получить latest snapshot;
- получить список repositories;
- storage metrics / repair.

### 2.3 Локальный orchestrator

- `src/Veyra.Infrastructure.Data/Sync/RepositoryCloudSyncOrchestrator.cs`

Именно orchestrator:

- решает, когда запускать sync;
- работает с локальной queue;
- обрабатывает retry;
- применяет conflict strategy;
- учитывает offline / paused state;
- умеет частично восстанавливать repositories из облака.

## 3. Реальные Go endpoint-ы

Регистрация endpoint-ов подтверждена в `server/cloud-api/cmd/api/main.go`.

| method | endpoint | назначение |
|---|---|---|
| `GET` | `/healthz` | health probe |
| `POST` | `/api/register` | регистрация |
| `POST` | `/api/login` | логин |
| `POST` | `/api/refresh` | обновление access token |
| `POST` | `/api/logout` | завершение сессии |
| `GET` | `/api/sync/repositories` | список облачных repositories |
| `DELETE` | `/api/sync/repositories/{repositoryId}` | удаление cloud repository |
| `GET` | `/api/sync/repositories/{repositoryId}/latest` | latest snapshot для repository |
| `POST` | `/api/sync/repositories/{repositoryId}/snapshots` | upload snapshot metadata |
| `HEAD` | `/api/sync/blocks/{blockHash}` | проверить наличие блока |
| `POST` | `/api/sync/blocks/batch` | batch upload block-ов |
| `POST` | `/api/sync/blocks/{blockHash}` | upload одного блока |
| `GET` | `/api/sync/blocks/{blockHash}` | download блока |
| `GET` | `/api/admin/storage/metrics` | cloud storage metrics |
| `POST` | `/api/admin/storage/repair` | storage repair |

## 4. Что хранится в PostgreSQL

Подтверждено в `server/cloud-api/migrations/0001_init.sql`.

Основные таблицы:

- `users`
- `devices`
- `user_sessions`
- `cloud_block_packs`
- `cloud_blocks`
- `repositories`
- `snapshots`
- `snapshot_entries`
- `snapshot_file_versions`
- `sync_idempotency_keys`
- `schema_migrations`

Практический смысл:

- PostgreSQL хранит cloud metadata и sync bookkeeping;
- сами file blocks не кладутся в PostgreSQL как BLOB-коллекция таблицы версий;
- для них есть отдельный cloud block storage layer.

## 5. Что хранится в cloud block storage

По inspected коду облако хранит:

- отдельные блоки (`cloud_blocks`);
- pack-агрегации (`cloud_block_packs`);
- служебные данные для compaction/metrics.

Важный момент:

- metadata о snapshot и file graph — в PostgreSQL;
- block payload — в файловом storage/packed storage.

## 6. Как происходит login и refresh token flow

### 6.1 Login

Client:

- вызывает `AuthHttpService`
- получает `AuthResponse`
- сохраняет session data в локальном profile storage

Server:

- endpoint в `main.go`
- обработчики в Go handlers

### 6.2 Где локально хранятся токены

По inspected коду:

- access token и refresh token сохраняются в `UserProfile`
- сущность: `src/Veyra.Domain/Entities/UserProfile.cs`
- репозиторий: `src/Veyra.Infrastructure.Data/Auth/UserProfiles/EfUserProfileRepository.cs`

То есть токены лежат в локальной SQLite-профильной модели.

Это нужно честно обозначать как security limitation.

### 6.3 Refresh token

`RepositoryCloudSyncOrchestrator` использует `TryGetSyncAccessTokenAsync(...)`:

- проверяет текущий access token;
- при необходимости инициирует refresh;
- если refresh отвергнут, выполняет local sign-out active profile.

Это подтверждает реальный жизненный цикл токена, а не “заглушку”.

## 7. Как snapshot metadata уходит в облако

Путь такой:

1. После локального snapshot orchestrator ставит операцию в queue.
2. При обработке queue вызывается HTTP push snapshot metadata.
3. Сервер принимает metadata через `/api/sync/repositories/{repositoryId}/snapshots`.
4. Сервер возвращает, каких block hash не хватает.
5. Клиент догружает только missing blocks.

Это видно в:

- `src/Veyra.Infrastructure.Data/Sync/RepositoryCloudSyncOrchestrator.cs`
- `src/Veyra.Infrastructure.Sync/Sync/Cloud/CloudSyncHttpService.cs`
- `server/cloud-api/internal/handlers/sync.go`

## 8. Как клиент понимает, какие блоки уже есть в облаке

Есть два механизма:

- `HEAD /api/sync/blocks/{blockHash}`
- `POST /api/sync/blocks/batch`

Практически pipeline настроен так:

- сначала отправляется snapshot metadata;
- сервер вычисляет `missingBlockHashes`;
- клиент отправляет только недостающие блоки.

Это означает, что в облаке тоже используется dedup-like модель по block hash.

## 9. Как работает sync queue

Локальная сущность:

- `src/Veyra.Domain/Entities/Repository/RepositorySyncQueueItem.cs`

Поля подтверждают queue-pattern:

- `OperationType`
- `Status`
- `ConflictStrategy`
- `AttemptCount`
- `MaxAttempts`
- `NextAttemptAtUtc`
- `ObservedRemoteSnapshotId`
- `LastError`
- `UploadCheckpointNextIndex`
- `UploadCheckpointTotal`
- `UploadCheckpointSignature`

Статусы:

- `pending`
- `running`
- `conflict`
- `retry`
- `completed`
- `failed`
- `dead_letter`
- `cancelled`

### Зачем это нужно

Queue отделяет:

- момент локального snapshot;
- момент реальной сети;
- момент успешной облачной фиксации.

Это и есть основа offline-first поведения.

## 10. Retry pattern и Polly

### 10.1 Polly

В `src/Veyra.Infrastructure.Sync` подключен `Microsoft.Extensions.Http.Polly`.

Следовательно, HTTP layer использует retry policy на уровне клиента.

### 10.2 Retry внутри orchestrator

Дополнительно есть application-level retry:

- `RepositoryCloudSyncOrchestrator.ComputeRetryDelay(...)`
- exponential backoff на основе `SyncRetryBaseDelaySeconds`

То есть retry реализован в двух слоях:

- transport-level;
- queue/orchestration-level.

### 10.3 Настройки retry

Подтверждены поля репозитория:

- `Repository.SyncRetryMaxAttempts`
- `Repository.SyncRetryBaseDelaySeconds`

и mapping в `VeyraDbContext`.

## 11. Conflict strategies

Подтверждены реальные стратегии:

- `last_write_wins`
- `manual_merge`
- `preserve_both`

Файл:

- `src/Veyra.Application/DTOs/Repository/Core/RepositorySyncConflictStrategies.cs`

Это значит, что стратегия конфликта — не просто идея на слайде, а часть persisted config и queue behavior.

### Что важно не преувеличивать

Наличие нескольких conflict strategies не означает, что реализован полноценный Git-like merge для бинарных данных. Реально это стратегия поведения sync queue и snapshot-level cloud coordination.

## 12. Offline-first и guest mode

## 12.1 Что работает без авторизации

По startup flow в `src/Veyra.Desktop/App.axaml.cs` без активного профиля продолжают работать:

- локальная SQLite база;
- создание и настройка repositories;
- локальные snapshots;
- локальный diff;
- restore;
- retention;
- monitoring;
- operation journal.

То есть core desktop-функции не зависят от облака.

### 12.2 Что происходит с cloud probing в guest mode

Ключевая строка:

- `connectivityService.SetCloudProbeEnabled(!string.IsNullOrWhiteSpace(activeUsername));`

Практический смысл:

- если нет активного пользователя, cloud probing выключен;
- guest mode не должен постоянно генерировать cloud warnings.

### 12.3 Состояния connectivity

`src/Veyra.Desktop/Services/Connectivity/ConnectivityStatusService.cs`

Подтверждены состояния:

- `Online`
- `CloudUnavailable`
- `InternetUnavailable`
- `Unknown`

### 12.4 Что происходит без интернета

Локальные операции продолжаются.

Cloud queue при этом:

- не теряется;
- не мешает локальным snapshots;
- обрабатывается позже после восстановления connectivity.

### 12.5 Что происходит после восстановления сети

После нормализации connectivity orchestrator может:

- продолжить `ProcessPendingQueueAsync()`;
- повторно использовать refresh token;
- догрузить очередные snapshot/block payload.

## 13. Restore from cloud: что реально реализовано

`RepositoryCloudSyncOrchestrator` умеет:

- `RestoreRepositoriesFromCloudAsync(...)`
- `RestoreRepositoryFromCloudAsync(...)`

Но важное ограничение:

- путь `restoreFullHistory = true` по inspected коду не реализован полностью;
- orchestrator пишет warning и возвращает отказ для full history restore.

Корректная защита:

- `в текущей версии реализовано восстановление repository и latest snapshot path`;
- `полный cloud history restore заявлен частично и требует доработки`.

## 14. gRPC: реальное состояние

В репозитории есть:

- `proto/sync.proto`
- `tools/sync-agent/cmd/sync-agent/main.go`

Но inspected код показывает:

- основной cloud sync pipeline работает через REST;
- gRPC agent выглядит как отдельный прототип;
- полноценной интеграции gRPC в production sync path не найдено.

Корректная формулировка:

- `gRPC присутствует как прототип/заготовка, но основная рабочая синхронизация сейчас REST-based`

## 15. Безопасность

## 15.1 Где используется AES-GCM

Подтверждено дважды:

- `src/Veyra.Infrastructure.Native/Security/Encryption/ArtifactBlockCryptor.cs`
- `src/Veyra.Infrastructure.Native/Security/MetadataProtection/CloudMetadataProtectionService.cs`

Назначение:

- шифрование artifact block payload в managed encrypted branch;
- защита отдельных cloud metadata значений.

## 15.2 Где хранится master key

- `src/Veyra.Infrastructure.Native/Security/KeyManagement/ArtifactMasterKeyStore.cs`

Путь:

- `%LOCALAPPDATA%/VeyraFlow/keys/artifact_master.protected`

Windows:

- используется DPAPI

не-Windows:

- есть plaintext fallback, что является ограничением.

## 15.3 Где хранится key ring

- `%LOCALAPPDATA%/VeyraFlow/keys/artifact_keyring.json`
- сервис: `ArtifactKeyManagementService`

## 15.4 Где хранится локальный пароль sensitive actions

- `src/Veyra.Desktop/Services/Auth/LocalCredentialStore.cs`
- файл `%LOCALAPPDATA%/VeyraFlow/local-credentials.bin`

По inspected коду:

- используется `PBKDF2-HMACSHA256`
- `150000` iterations
- на Windows — DPAPI-protected payload
- на не-Windows — plaintext fallback protector

## 15.5 Как хешируются пароли на сервере

Go server использует:

- `bcrypt.GenerateFromPassword(..., bcrypt.DefaultCost)`

Это подтверждено в server-side auth flow.

## 15.6 Как устроены refresh token-ы на сервере

По inspected коду:

- refresh tokens генерируются как opaque random tokens;
- в БД сохраняется SHA-256 hash refresh token, а не чистое значение.

Это хорошая практика по сравнению с хранением refresh token в открытом виде на сервере.

## 15.7 Отправляются ли локальные пути в облако

В коде есть слой `CloudMetadataProtectionService` и флаг `Repository.ProtectCloudMetadata`.

Это означает:

- разработчик осознает риск утечки metadata;
- но нельзя утверждать, что вообще никакие path-like значения никогда не покидают клиент без дополнительной проверки каждого payload.

Корректная формулировка:

- `есть механизм защиты облачных metadata значений, но локальная БД и часть sync metadata требуют аккуратного security review перед production`.

## 15.8 TLS assumptions

Локальный demo-путь явно не production-hardened:

- default DSN использует `sslmode=disable`
- `AUTH_TOKEN_SECRET` имеет dev default в `server/cloud-api/cmd/api/main.go`

Следовательно:

- в локальной среде безопасность транспорта предполагается условной;
- перед production требуется обязательный TLS и нормальное secret management.

## 16. Security risks

### Ключевые риски

| риск | что подтверждено кодом | последствия | что улучшить |
|---|---|---|---|
| token leakage | access/refresh token лежат в `UserProfile` SQLite | компрометация cloud session | вынести токены в platform secure storage |
| local DB exposure | SQLite metadata не зашифрована целиком | утечка истории, путей, sync state | добавить DB encryption / split secret storage |
| cloud metadata leakage | часть metadata требует отдельной защиты | раскрытие имен и путей | расширить metadata protection coverage |
| block tampering | для native block verification есть integrity gap | риск незамеченного повреждения блока | добавить симметричную BLAKE3 payload recheck |
| missing TLS in demo defaults | `sslmode=disable`, dev secret | MitM и session compromise | mandatory HTTPS/TLS и secret rotation |
| plaintext fallback on non-Windows | secret protection упрощается | локальная компрометация ключей | внедрить platform-specific keystore |
| replay / conflict complexity | queue и idempotency есть, но сценарии сложны | sync anomalies | расширить integration tests и observability |
| secrets in config | dev defaults в коде/конфиге | небезопасный деплой | env-based secret provisioning |

## 17. Что можно уверенно говорить на защите

- Проект действительно offline-first: локальные snapshots и restore не требуют облака.
- Cloud sync действительно реализован через REST API на Go.
- Есть локальная sync queue с retry и conflict strategy.
- Есть refresh token flow и connectivity-aware orchestration.
- Есть отдельные security-механизмы: AES-GCM, DPAPI, PBKDF2, bcrypt.

## 18. Что нужно формулировать осторожно

- Не говорить, что вся облачная часть уже production-ready.
- Не говорить, что gRPC полноценно внедрен в основной pipeline.
- Не говорить, что токены хранятся в идеальном secure vault на всех платформах.
- Не говорить, что full history restore из облака уже завершен.

## 19. Ключевой вывод для защиты

Облачная подсистема VeyraFlow в inspected коде — это реальная, но честно промежуточная production-grade архитектура:

- локальная desktop-логика полностью самостоятельна;
- при наличии авторизации и сети включается REST-based cloud sync;
- sync работает через queue, retry и block-level upload;
- offline-first и guest mode подтверждены кодом;
- security-механизмы есть, но часть из них требует усиления перед production.

Именно такая формулировка технически точна и хорошо защищается перед комиссией.
