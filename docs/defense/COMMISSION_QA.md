# Вопросы и ответы для комиссии

## Позиционирование и актуальность

1. **Вопрос:** Почему тема проекта актуальна?  
   **Ответ:** Пользователи постоянно изменяют документы, код, изображения и другие файлы, но обычная файловая система не хранит удобную историю состояния. Проект решает задачу локального контроля версий и восстановления без обязательной зависимости от интернета.

2. **Вопрос:** Это файловый менеджер?  
   **Ответ:** Нет, корректнее называть VeyraFlow desktop-приложением для отслеживания состояния выбранных файлов и папок, создания snapshots, сравнения версий и восстановления файлов.

3. **Вопрос:** Чем проект отличается от Windows Explorer?  
   **Ответ:** Explorer ориентирован на навигацию и операции над файлами. VeyraFlow ориентирован на историю версий, snapshots, deduplication, diff и restore.

4. **Вопрос:** Чем проект отличается от обычного backup?  
   **Ответ:** Backup обычно делает резервную копию или образ, а VeyraFlow хранит историю версий, умеет сравнивать состояния и восстанавливать отдельные файлы.

5. **Вопрос:** Чем проект отличается от OneDrive?  
   **Ответ:** OneDrive в первую очередь решает задачу облачной синхронизации. VeyraFlow в inspected коде строится как offline-first система локальных snapshots с дополнительной cloud sync-функцией.

6. **Вопрос:** Чем проект отличается от Git?  
   **Ответ:** Git оптимален для текстового кода и репозиториев разработчиков. VeyraFlow ориентирован на смешанные типы файлов, block storage и пользовательский desktop workflow.

7. **Вопрос:** Почему не использовать просто File History?  
   **Ответ:** File History больше похож на резервное копирование. VeyraFlow добавляет version graph, diff-представления и более явную модель snapshots.

8. **Вопрос:** Почему проект не позиционируется как замена проводника?  
   **Ответ:** Потому что inspected код не подтверждает полный набор функций классического file explorer: копирование, перемещение, переименование, drag-and-drop управление файлами и подобные операции не являются ядром продукта.

9. **Вопрос:** Какая практическая польза пользователю?  
   **Ответ:** Пользователь может отслеживать изменения важных директорий, видеть историю версий, сравнивать состояния файлов и быстро восстанавливать нужную версию.

10. **Вопрос:** Какая главная идея проекта одной фразой?  
   **Ответ:** Это offline-first система snapshots и истории версий для выбранных файлов и папок.

## Snapshot, сущности и жизненный цикл

11. **Вопрос:** Что такое snapshot?  
   **Ответ:** Snapshot — это снимок состояния репозитория в конкретный момент времени: набор файлов, их метаданных и ссылок на конкретные версии содержимого.

12. **Вопрос:** Что такое repository в проекте?  
   **Ответ:** Repository — это логическая единица наблюдения за одной выбранной директорией с ее настройками, политиками retention и cloud sync state.

13. **Вопрос:** Что такое FileIdentity?  
   **Ответ:** `FileIdentity` — это логическая карточка файла внутри repository, обычно связанная с относительным путем, именем и расширением.

14. **Вопрос:** Что такое FileVersion?  
   **Ответ:** `FileVersion` — это конкретное содержимое файла в определенный момент времени, с hash, размером и временем изменения.

15. **Вопрос:** Что такое FileVersionBlock?  
   **Ответ:** `FileVersionBlock` — это метаданные одного блока файла: его порядок, ключ хранения, длина и размер в stored form.

16. **Вопрос:** Зачем разделять FileIdentity и FileVersion?  
   **Ответ:** Это позволяет одной логической сущности файла иметь много исторических версий без дублирования всей identity-информации.

17. **Вопрос:** Где в коде создается snapshot?  
   **Ответ:** Основная persistence-логика находится в `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`, метод `SaveSnapshotAsync(...)`.

18. **Вопрос:** Кто инициирует scan и snapshot из UI?  
   **Ответ:** `RepositoryExplorerViewModel` отправляет `ScanRepositoryCommand`, который обрабатывает `ScanRepositoryHandler`.

19. **Вопрос:** Что происходит после выбора директории пользователем?  
   **Ответ:** Путь валидируется, создается или активируется repository, сохраняются watched settings, затем выполняется первичное сканирование и создается начальный snapshot.

20. **Вопрос:** Как snapshot связан с SQLite?  
   **Ответ:** В SQLite сохраняются `RepositorySnapshot`, `RepositorySnapshotEntry`, `FileIdentity`, `FileVersion`, `FileVersionBlock` и связанные сущности, а сами block payload лежат в файловом storage.

## Дедупликация и storage

21. **Вопрос:** Что такое дедупликация в этом проекте?  
   **Ответ:** Это повторное использование уже существующих блоков данных, чтобы не хранить одинаковые части файлов заново.

22. **Вопрос:** Почему используется блочная дедупликация, а не полные копии?  
   **Ответ:** Потому что при небольших изменениях нет смысла хранить весь файл заново; достаточно сохранить только новые блоки и сослаться на старые.

23. **Вопрос:** Какой размер блока используется?  
   **Ответ:** По inspected коду используется фиксированный размер `64 KiB`.

24. **Вопрос:** Это фиксированный размер или content-defined chunking?  
   **Ответ:** В текущей inspected версии — фиксированный размер блока, content-defined chunking не найден.

25. **Вопрос:** Зачем нужен SHA-256?  
   **Ответ:** SHA-256 используется для whole-file content hash и в managed encrypted branch для block key based on stored payload.

26. **Вопрос:** Зачем нужен BLAKE3?  
   **Ответ:** BLAKE3 используется в native block storage как быстрый hash для block-level deduplication.

27. **Вопрос:** Почему в проекте сразу два hash-алгоритма?  
   **Ответ:** Они решают разные задачи: SHA-256 — целостность версии файла, BLAKE3 — быстрый ключ native block storage.

28. **Вопрос:** Где реально используется BLAKE3?  
   **Ответ:** В `native/veyra_core/src/block_store/mod.rs` для вычисления block key чанка.

29. **Вопрос:** Где реально используется SHA-256?  
   **Ответ:** В `FileVersion.ContentHashSha256`, в managed branch block key и в финальной проверке restore.

30. **Вопрос:** Как система понимает, что блок уже существует?  
   **Ответ:** По вычисленному block key строится путь хранения, и если block file уже есть, он переиспользуется вместо повторной записи.

31. **Вопрос:** Где физически лежат блоки?  
   **Ответ:** В локальном block storage на файловой системе, а не в SQLite; native path использует структуру каталогов по префиксам hash.

32. **Вопрос:** Сжимается ли блок?  
   **Ответ:** Да, в native ветке используется zstd, а в managed encrypted branch — Brotli.

33. **Вопрос:** Шифруются ли блоки?  
   **Ответ:** Да, в managed artifact encryption branch block payload защищается через AES-GCM.

34. **Вопрос:** Хранится ли compression ratio отдельным полем?  
   **Ответ:** Отдельного persisted поля для ratio не найдено, но его можно вычислить по `LengthBytes` и `StoredSizeBytes`.

35. **Вопрос:** Как дедупликация работает между разными файлами?  
   **Ответ:** Если блоки содержательно совпадают и дают один key, они могут быть переиспользованы даже между разными файлами одного storage.

## Restore и целостность

36. **Вопрос:** Как восстанавливается файл?  
   **Ответ:** Система берет список блоков версии, сортирует их по `Sequence`, читает каждый block payload, при необходимости распаковывает или расшифровывает его и записывает байты в выходной файл.

37. **Вопрос:** Почему блоки сортируются по Sequence?  
   **Ответ:** Потому что только так можно восстановить исходный порядок байтов файла.

38. **Вопрос:** Что означает overwrite existing?  
   **Ответ:** Это режим, в котором восстановленная версия может заменить уже существующий target-файл.

39. **Вопрос:** Можно ли восстановить файл в новый путь?  
   **Ответ:** Да, restore может работать как в новый файл, так и в режим замены текущего файла.

40. **Вопрос:** Как проверяется, что restore корректен?  
   **Ответ:** После сборки файла система может вычислить итоговый SHA-256 и сравнить его с ожидаемым `ContentHashSha256`.

41. **Вопрос:** Что будет, если блок поврежден?  
   **Ответ:** Restore может завершиться ошибкой decode/decompress/decrypt или final hash mismatch.

42. **Вопрос:** Есть ли отдельная проверка целостности storage?  
   **Ответ:** Да, есть `EfRepositoryIntegrityService`, но для native BLAKE3 blocks там есть важное ограничение: повторная payload verification не столь симметрична, как для managed `sha256-*` блоков.

43. **Вопрос:** Что происходит при удалении snapshot?  
   **Ответ:** Snapshot сначала soft-delete или архивируется, затем удаляются зависимые ссылки и только после этого могут удаляться неиспользуемые block files.

44. **Вопрос:** Когда блок можно физически удалить?  
   **Ответ:** Только когда он больше не referenced ни одной актуальной file version.

45. **Вопрос:** Что будет при hash collision?  
   **Ответ:** Специальной collision-resolution логики не найдено; система опирается на криптографическую устойчивость SHA-256 и BLAKE3.

## Архитектура и паттерны

46. **Вопрос:** Какая архитектура используется?  
   **Ответ:** Многослойная архитектура с разделением на Presentation, Application, Domain, Infrastructure, Native и Cloud layers.

47. **Вопрос:** Где находится UI layer?  
   **Ответ:** В `src/Veyra.Desktop`.

48. **Вопрос:** Где находится Application layer?  
   **Ответ:** В `src/Veyra.Application`.

49. **Вопрос:** Где находится Domain layer?  
   **Ответ:** В `src/Veyra.Domain`.

50. **Вопрос:** Где находится Infrastructure layer?  
   **Ответ:** В `src/Veyra.Infrastructure.Data`, `src/Veyra.Infrastructure.Native` и `src/Veyra.Infrastructure.Sync`.

51. **Вопрос:** Почему Domain не должен знать про Infrastructure?  
   **Ответ:** Чтобы бизнес-модель и ключевые сущности не зависели от конкретной БД, HTTP-клиента, native interop или UI.

52. **Вопрос:** Почему UI не работает напрямую с SQLite?  
   **Ответ:** Потому что UI должен обращаться к use case-ам через Application layer, а не смешивать presentation и persistence concerns.

53. **Вопрос:** Почему UI не вызывает Rust напрямую?  
   **Ответ:** Потому что native interop инкапсулирован в infrastructure adapters, а UI работает через abstractions и MediatR-команды.

54. **Вопрос:** Где используется MVVM?  
   **Ответ:** В `src/Veyra.Desktop/Views/*` и `src/Veyra.Desktop/ViewModels/*`.

55. **Вопрос:** Где используется CQRS?  
   **Ответ:** В разделении на `Commands/*` и `Queries/*` внутри `src/Veyra.Application`.

56. **Вопрос:** Где используется MediatR?  
   **Ответ:** В Application layer для dispatch `Command Handler / Query Handler` логики.

57. **Вопрос:** Где используется DI?  
   **Ответ:** В нескольких composition root файлах: `src/Veyra.Application/DependencyInjection.cs`, `src/Veyra.Infrastructure.* /DependencyInjection*.cs`, `src/Veyra.Desktop/CompositionRoot/DependencyInjection*.cs`.

58. **Вопрос:** Где используется Adapter pattern?  
   **Ответ:** На границе с Rust и cloud, например `RustRepositoryScanner`, `RustFileContentStore`, `CloudSyncHttpService`, `AuthHttpService`.

59. **Вопрос:** Где используется Repository pattern?  
   **Ответ:** Например `EfRepositoryRepository`, `EfSetupRepository`, `EfRepositorySnapshotRepository`.

60. **Вопрос:** Где здесь Unit of Work?  
   **Ответ:** Роль Unit of Work выполняет `VeyraDbContext` как EF Core transaction/persistence boundary.

61. **Вопрос:** Где используется Validation pipeline?  
   **Ответ:** В `ValidationBehavior<TReq,TRes>` и FluentValidation validators.

62. **Вопрос:** Где используется Logging pipeline?  
   **Ответ:** В `LoggingBehavior<TReq,TRes>` и structured logging setup.

63. **Вопрос:** Где используется Operation Journal?  
   **Ответ:** В `OperationJournalBehavior<TRequest,TResponse>` и сущности `OperationJournalEntry`.

64. **Вопрос:** Где используется Queue pattern?  
   **Ответ:** В `RepositorySyncQueueItem` для cloud sync и в `RepositoryFsEventQueueService` для filesystem events.

65. **Вопрос:** Где используется soft delete?  
   **Ответ:** Во многих EF-сущностях с `IsDeleted` и `HasQueryFilter(...)` в `VeyraDbContext`.

## Форматы, diff и preview

66. **Вопрос:** Какие diff-режимы наиболее зрелые?  
   **Ответ:** Text diff и image diff.

67. **Вопрос:** Как работает text diff?  
   **Ответ:** Восстанавливаются две версии, текст сравнивается line-level engine-ом, а результат может кэшироваться в SQLite как hunks и lines.

68. **Вопрос:** Поддерживается ли Word semantic diff?  
   **Ответ:** Частично: есть `WordSemanticProjection` для OOXML и отдельный Windows-specific launch native compare через Microsoft Word.

69. **Вопрос:** Как работает image diff?  
   **Ответ:** Сравниваются два изображения, считается количество измененных пикселей и регионов, а результат визуализируется как overlay, heatmap, split или composite.

70. **Вопрос:** Какие форматы реально поддерживает image diff?  
   **Ответ:** `png`, `jpg`, `jpeg`, `bmp`, `gif`, `webp`, `tif`, `tiff`, `svg`, но для animated GIF лучше говорить осторожно.

71. **Вопрос:** Как работает audio diff?  
   **Ответ:** Для части аудиоформатов строятся waveform, spectrogram, spectral delta, difference audio и метрики similarity.

72. **Вопрос:** Все ли заявленные аудиоформаты имеют специализированный diff?  
   **Ответ:** Нет, `flac` и `ogg` по текущему routing идут как generic binary, а не specialized audio diff.

73. **Вопрос:** Что такое generic binary mode?  
   **Ответ:** Это режим, в котором файл хранится и восстанавливается нормально, но сравнение ограничивается binary summary, размером, hash и overlap блоков.

74. **Вопрос:** Есть ли specialized archive diff?  
   **Ответ:** Подтвержден только для ZIP.

75. **Вопрос:** Есть ли PDF preview или PPTX diff?  
   **Ответ:** По inspected коду готового specialized PDF/PPTX preview-diff не найдено; они идут как binary storage/restore cases.

## Cloud, offline-first и guest mode

76. **Вопрос:** Что работает без интернета?  
   **Ответ:** Локальные repositories, snapshots, diff, restore, retention, monitoring и journal.

77. **Вопрос:** Что работает без авторизации?  
   **Ответ:** Практически все локальное ядро, кроме cloud sync-функций.

78. **Вопрос:** Что такое guest mode?  
   **Ответ:** Это режим без активного cloud profile, в котором облачные предупреждения не должны мешать локальной работе.

79. **Вопрос:** Как система понимает, что облако недоступно?  
   **Ответ:** Через `ConnectivityStatusService` и health probing `/healthz`.

80. **Вопрос:** Какие состояния connectivity есть?  
   **Ответ:** `Online`, `CloudUnavailable`, `InternetUnavailable`, `Unknown`.

81. **Вопрос:** Что происходит с sync queue офлайн?  
   **Ответ:** Операции остаются в очереди и обрабатываются позже после восстановления связи.

82. **Вопрос:** Как реализован cloud API?  
   **Ответ:** Основная рабочая реализация — REST API на Go.

83. **Вопрос:** Используется ли gRPC?  
   **Ответ:** В репозитории есть `proto/sync.proto` и прототип `tools/sync-agent`, но основной production path сейчас REST, а не gRPC.

84. **Вопрос:** Какой конфликт-менеджмент есть в cloud sync?  
   **Ответ:** Есть стратегии `last_write_wins`, `manual_merge`, `preserve_both`.

85. **Вопрос:** Реализован ли полный restore всей cloud history?  
   **Ответ:** Нет, full history restore по inspected коду частично заявлен, но не завершен.

## Базы данных, безопасность и эксплуатация

86. **Вопрос:** Что хранится в SQLite?  
   **Ответ:** Локальные metadata: repositories, snapshots, file identities, file versions, block references, diff cache, queue, journal, user profiles и watched setup.

87. **Вопрос:** Что хранится в PostgreSQL?  
   **Ответ:** Cloud metadata: users, sessions, cloud repositories, snapshots, entries, block references, idempotency keys и storage bookkeeping.

88. **Вопрос:** Шифруется ли локальная БД целиком?  
   **Ответ:** По inspected коду нет подтверждения полного DB encryption; шифрование применяется точечно для artifact blocks и части metadata.

89. **Вопрос:** Где хранится access token?  
   **Ответ:** В локальном `UserProfile`, что является одним из security risks текущей версии.

90. **Вопрос:** Как хешируется пароль на сервере?  
   **Ответ:** На Go side используется bcrypt.

91. **Вопрос:** Где используется AES-GCM?  
   **Ответ:** В `ArtifactBlockCryptor` для block payload encryption и в `CloudMetadataProtectionService` для защиты metadata values.

92. **Вопрос:** Как хранится master key?  
   **Ответ:** В локальном protected file; на Windows используется DPAPI, на не-Windows есть plaintext fallback, что требует усиления.

93. **Вопрос:** Есть ли TLS assumptions в demo-конфигурации?  
   **Ответ:** Да, по умолчанию в dev-конфигурации есть `sslmode=disable`, поэтому production-grade security отдельно не заявляется.

94. **Вопрос:** Почему security нельзя называть полностью production-ready?  
   **Ответ:** Потому что есть token storage в SQLite, plaintext fallback на не-Windows и незавершенный hardening transport/secrets.

## Тестирование, ограничения и развитие

95. **Вопрос:** Какие тесты есть в проекте?  
   **Ответ:** Есть .NET tests для application, data и desktop, а также Rust unit tests в native core; Go `_test.go` files по inspected репозиторию не найдены.

96. **Вопрос:** Что покрыто тестами особенно полезно?  
   **Ответ:** Scan handler, restore integrity, Word semantic projection, retention quality gates, audio diff preview, SVG structural diff, image diff pipeline и filesystem event queue.

97. **Вопрос:** Какие слабые места в test coverage?  
   **Ответ:** Не хватает более глубоких integration tests для cloud API, full restore scenarios, native block corruption handling и cross-platform security behavior.

98. **Вопрос:** Что бы вы доработали первым после защиты?  
   **Ответ:** Исправил бы presentation wording в продуктовой документации, усилил native block integrity verification, завершил full cloud history restore и расширил format-specific preview.

99. **Вопрос:** Что можно быстро улучшить до защиты?  
   **Ответ:** Обновить слайды, сделать сильный demo dataset, добавить реальные screenshots приложения и четко разделить `реализовано / частично реализовано / generic`.

100. **Вопрос:** Какая самая честная итоговая оценка проекта?  
   **Ответ:** У проекта уже есть сильное рабочее локальное ядро snapshots, deduplication, restore и diff, но часть продвинутых функций облака и сложных форматов еще находится в стадии частичной реализации.
