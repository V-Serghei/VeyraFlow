# Roadmap, ограничения и честная оценка готовности

## 1. Что уже реализовано и подтверждено кодом

### Локальное ядро

- создание и конфигурация repositories через `CreateRepositoryWithFormatsHandler` и `SaveInitialSetupHandler`
- хранение setup в SQLite через `EfSetupRepository`
- snapshots и version history через `EfRepositorySnapshotRepository`
- `FileIdentity`, `FileVersion`, `FileVersionBlock` граф версий
- block-level deduplication
- restore файла по списку блоков
- проверка итогового SHA-256 после restore

### UI и application architecture

- Avalonia UI с MVVM
- navigation и window services
- CQRS + MediatR
- validation/logging/operation journal pipeline behaviors
- repository explorer, settings, dashboard, search, compare windows

### Diff и preview

- text diff
- image diff
- SVG structural diff
- ZIP archive diff
- partial audio diff preview
- generic binary compare summary

### Background и обслуживание

- snapshot scheduler
- filesystem watcher queue
- retention и cleanup
- operation journal
- startup health-check / recovery

### Cloud и offline

- guest mode
- connectivity-aware probing
- REST cloud API
- login / refresh / logout
- sync queue
- block upload / batch upload
- partial restore from cloud

## 2. Что реализовано частично

### Word OOXML semantic diff

Состояние:

- `частично реализовано`

Почему:

- `WordSemanticProjection.cs` есть;
- COM launch compare через Microsoft Word есть;
- но полноценного собственного deep semantic/visual Word diff внутри приложения не найдено.

### Audio support

Состояние:

- `частично реализовано`

Почему:

- есть `AudioDiffPreviewBuilder`;
- но специализированный routing ограничен `wav/mp3/aac/m4a/wma/aif/aiff`;
- `flac/ogg` не routed в specialized audio diff;
- decoding зависит от `NAudio`.

### Cloud restore

Состояние:

- `частично реализовано`

Почему:

- restore latest/local relink paths есть;
- full history restore по inspected коду не завершен.

### Archive support

Состояние:

- `частично реализовано`

Почему:

- специализированный archive diff подтвержден только для ZIP;
- остальные архивы идут как generic binary.

## 3. Что лучше не заявлять как полностью готовое

- `полноценный файловый менеджер`
- `замена Windows Explorer`
- `полный semantic diff Office документов`
- `универсальный multimedia diff для всех форматов`
- `полный cloud history restore`
- `gRPC как основной production transport`
- `повсеместное использование Dapper`
- `полностью production-ready security posture`

## 4. Critical findings, которые нельзя скрывать

### 1. Integrity gap для native blocks

`src/Veyra.Infrastructure.Data/Setup/Integrity/EfRepositoryIntegrityService.cs`

Проблема:

- managed `sha256-*` blocks пересчитываются;
- для native BLAKE3 blocks нет симметричной полной payload verification logic в этой службе.

### 2. Token storage в SQLite profile

`src/Veyra.Domain/Entities/UserProfile.cs`

Проблема:

- access/refresh tokens лежат в локальной profile-модели, а не в отдельном secure vault.

### 3. Full history cloud restore не завершен

`src/Veyra.Infrastructure.Data/Sync/RepositoryCloudSyncOrchestrator.cs`

Проблема:

- full history restore path явно не готов.

### 4. Презентация завышает позиционирование

Проблема:

- термин `файловый менеджер` не соответствует реальному коду и создает лишние вопросы.

## 5. Что срочно поправить перед защитой

### Документация и слайды

- убрать `файловый менеджер` со всех слайдов
- явно назвать проект системой snapshots и истории версий
- исправить слайд форматов
- исправить слайд технологий: REST как основной cloud path, gRPC как prototype
- добавить реальные screenshots приложения

### Демонстрационный сценарий

- показывать создание repository
- показывать snapshot history
- показывать text diff или image diff
- показывать restore
- если есть cloud demo, показывать его только как дополнительную функцию

### Темы, которые нужно выучить

- дедупликация по блокам
- отличие `FileIdentity` от `FileVersion`
- различие `SHA-256` и `BLAKE3`
- почему 64 KiB blocks
- как файл собирается обратно при restore
- что работает offline
- что именно реализовано частично

## 6. Что можно быстро доработать до защиты

### Высокий эффект / умеренная стоимость

- обновить презентацию под корректное позиционирование
- сделать сильный demo dataset: текстовый файл, изображение, аудио и binary case
- добавить на слайд результатов реальные screenshot-ы
- добавить на форматы slide маркировку `specialized / partial / generic`
- при желании добавить PDF preview-only path
- при желании добавить CSV/XLSX preview-only path

### Низкий риск

- улучшить README/docs навигацию
- отдельно вынести схему `snapshot -> file version -> blocks`
- подготовить короткие ответы про частичную готовность Word/audio/cloud history

## 7. Что оставить как future work после защиты

### Форматы и diff

- structured diff для JSON/XML/YAML
- richer Word semantic diff
- preview для PDF/PPTX/XLSX
- archive content diff для `7z/rar/tar/gz`
- FLAC/OGG specialized audio diff
- metadata preview для video formats

### Cloud

- полноценный full history restore
- более развитые conflict-resolution сценарии
- расширенные integration tests по queue/replay/recovery

### Security

- secure token storage вне SQLite
- безусловный TLS-only режим
- усиленная native block integrity verification
- platform-specific keystore вместо plaintext fallback

## 8. Production-level improvements

### Надежность

- end-to-end test matrix для snapshot/restore/dedup
- stronger corruption detection for native blocks
- observability по cloud sync и retention
- explicit migration strategy и backup/repair tooling

### Безопасность

- encrypted local metadata
- rotated secrets and managed secret store
- server-side rate limiting / security hardening
- stricter password policy
- audit trail для auth events

### UX

- clearer onboarding для guest mode vs cloud mode
- richer repository health visualization
- format-specific preview badges
- better explanation of generic binary mode

## 9. Что улучшит демонстрацию сильнее всего

### Самые выгодные сценарии

1. Изменить текстовый файл и показать line diff.
2. Изменить изображение и показать overlay/split.
3. Создать два snapshot одного большого файла и объяснить dedup по блокам.
4. Восстановить предыдущую версию файла и показать hash-verified restore.

### Что не стоит делать как основной акцент

- пытаться показать проект как универсальный проводник;
- перегружать защиту облаком, если локальное ядро и так сильное;
- обещать поддержку форматов, которые по коду идут только в generic binary.

## 10. Честная итоговая формулировка

`VeyraFlow` уже сейчас выглядит как технически содержательный desktop-проект с реальным локальным ядром: snapshots, version history, block storage, deduplication, restore, diff, background monitoring и offline-first поведением. При этом часть продвинутых функций еще не завершена полностью. Для защиты это не минус, если эти ограничения не скрывать, а использовать как осознанный roadmap развития.
