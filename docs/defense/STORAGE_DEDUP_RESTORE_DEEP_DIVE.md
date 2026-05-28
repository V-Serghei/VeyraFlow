# Snapshot, storage, дедупликация и restore: deep dive

## Основание для выводов

Этот документ составлен по реальному коду, а не по общим архитектурным слайдам. Ключевые файлы, на которые опирается анализ:

- `src/Veyra.Desktop/ViewModels/Pages/Explorer/RepositoryExplorerViewModel.cs`
- `src/Veyra.Application/Commands/Repository/ScanRepositoryHandler.cs`
- `src/Veyra.Application/Commands/Repository/RestoreFileVersionHandler.cs`
- `src/Veyra.Infrastructure.Native/Scanning/RustRepositoryScanner.cs`
- `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`
- `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`
- `src/Veyra.Infrastructure.Native/Interop/VeyraCoreNative.cs`
- `src/Veyra.Infrastructure.Native/Security/Encryption/ArtifactBlockCryptor.cs`
- `native/veyra_core/src/block_store/mod.rs`
- `native/veyra_core/src/ffi/api.rs`
- `src/Veyra.Domain/Entities/FileVersioning/FileIdentity.cs`
- `src/Veyra.Domain/Entities/FileVersioning/FileVersion.cs`
- `src/Veyra.Domain/Entities/FileVersioning/FileVersionBlock.cs`

## 1. Полная цепочка создания snapshot

### 1.1 Откуда snapshot запускается в UI

Реальный пользовательский запуск snapshot находится в `src/Veyra.Desktop/ViewModels/Pages/Explorer/RepositoryExplorerViewModel.cs`.

Ключевая цепочка:

1. Пользователь открывает экран репозитория.
2. `RepositoryExplorerViewModel` показывает pending preview через `GetPendingFileDiffPreviewQuery`.
3. После подтверждения отправляется `ScanRepositoryCommand`.
4. Для ручного snapshot задаются `Trigger`, `Title`, `Tags`, `SaveFileVersions = true`.

Это важно для защиты: snapshot создается не напрямую из UI в базу, а через CQRS-команду Application layer.

### 1.2 Какой handler запускает реальное сканирование

Команда обрабатывается в `src/Veyra.Application/Commands/Repository/ScanRepositoryHandler.cs`.

Handler:

- получает `IRepositoryScanner`;
- вызывает scan use case;
- возвращает `RepositoryScanResultDto`;
- если snapshot реально создан и это versioned scan, фоново инициирует `IRepositoryCloudSyncOrchestrator.TryPushLatestSnapshotAsync(repositoryId)`.

То есть snapshot и cloud push разделены: сначала локальная фиксация, потом попытка синхронизации.

### 1.3 Какой сервис реально сканирует репозиторий

Основная реализация `IRepositoryScanner` подменяется native-адаптером:

- интерфейс объявляется в Application layer;
- реализация находится в `src/Veyra.Infrastructure.Native/Scanning/RustRepositoryScanner.cs`.

Что делает `RustRepositoryScanner`:

- блокирует конкурентные scan-операции по одному repository;
- собирает конфигурацию scan;
- пытается вызвать native engine через `VeyraCoreNative.ScanDirectoryJson(...)`;
- при невозможности использует managed fallback;
- при `SaveFileVersions = true` передает результат в `IRepositorySnapshotRepository.SaveSnapshotAsync(...)`.

### 1.4 Где определяется список файлов

Список файлов формируется в `RustRepositoryScanner`:

- native-путь: через Rust scan engine `native/veyra_core/src/scan/mod.rs`;
- fallback-путь: через обход каталогов в C# внутри `RustRepositoryScanner`.

На этом этапе применяются:

- tracked formats из `WatchedDirectory`, `D_WatchedFormat`, `WatchedDirectoryFormat`;
- исключения репозитория;
- фильтрация по расширениям;
- нормализация относительных путей.

### 1.5 Где проверяются tracked formats и exclusion rules

Реальные точки:

- `src/Veyra.Application/Common/Files/KnownFileExtensions.cs`
- `src/Veyra.Infrastructure.Native/Scanning/RepositoryScanExclusionMatcher.cs`
- `src/Veyra.Infrastructure.Data/Setup/EfSetupRepository.cs`
- `src/Veyra.Application/Commands/Repository/CreateRepositoryWithFormatsHandler.cs`
- `src/Veyra.Application/Commands/Repository/UpdateRepositoryConfigurationHandler.cs`

Важно:

- отдельной таблицы `ExclusionRule` в inspected коде не найдено;
- исключения хранятся в `Repository.ExclusionPatternsJson` в `src/Veyra.Domain/Entities/Repository/Repository.cs`;
- это нужно формулировать на защите честно: исключения реализованы, но не как отдельная нормализованная сущность БД.

### 1.6 Как определяется, что файл изменился

Изменение определяется не по одному timestamp, а по комбинации метаданных и содержимого:

- scan-слой собирает текущее состояние файлов;
- comparison engine сравнивает baseline и current;
- в локальной persistence-цепочке используется `ISnapshotComparisonEngine`, который в native-режиме реализован через `src/Veyra.Infrastructure.Native/Diffing/RustSnapshotComparisonEngine.cs`, а fallback-реализация находится в `src/Veyra.Application/Services/ManagedSnapshotComparisonEngine.cs`;
- для файла сохраняется `ContentHashSha256`, размер и `LastWriteUtc`.

## 2. Где snapshot попадает в SQLite

Основной persistence entrypoint:

- `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`
- метод `SaveSnapshotAsync(...)`

Что делает `SaveSnapshotAsync(...)`:

1. Загружает предыдущее состояние репозитория.
2. Вычисляет изменения относительно baseline.
3. Создает `RepositorySnapshot`.
4. Создает `RepositorySnapshotEntry` по текущей структуре.
5. Находит или создает `FileIdentity`.
6. Для изменившихся файлов создает `FileVersion`.
7. Для каждого сохраненного файла создает набор `FileVersionBlock`.
8. Создает `SnapshotFileLink`, связывающий snapshot, file identity и конкретную file version.
9. Для text-diff-поддерживаемых форматов может заранее кэшировать diff в таблицах text diff.

Связанные сущности:

- `src/Veyra.Domain/Entities/Repository/RepositorySnapshot.cs`
- `src/Veyra.Domain/Entities/Repository/RepositorySnapshotEntry.cs`
- `src/Veyra.Domain/Entities/Repository/SnapshotFileLink.cs`
- `src/Veyra.Domain/Entities/FileVersioning/FileIdentity.cs`
- `src/Veyra.Domain/Entities/FileVersioning/FileVersion.cs`
- `src/Veyra.Domain/Entities/FileVersioning/FileVersionBlock.cs`

## 3. Роль FileIdentity, FileVersion и FileVersionBlock

### 3.1 FileIdentity

`src/Veyra.Domain/Entities/FileVersioning/FileIdentity.cs`

`FileIdentity` представляет логическую сущность файла внутри репозитория:

- `RepositoryId`
- `RelativePath`
- `Name`
- `Extension`

Смысл:

- один и тот же путь в репозитории имеет одну identity;
- разные версии этого же файла привязываются к одной identity.

### 3.2 FileVersion

`src/Veyra.Domain/Entities/FileVersioning/FileVersion.cs`

`FileVersion` описывает конкретное содержимое файла:

- `ContentHashSha256`
- `SizeBytes`
- `LastWriteUtc`
- `IsDeletionMarker`
- `CreatedAt`

Смысл:

- `FileIdentity` отвечает на вопрос «какой это файл по пути»;
- `FileVersion` отвечает на вопрос «какое именно содержимое у него было в этот момент».

### 3.3 FileVersionBlock

`src/Veyra.Domain/Entities/FileVersioning/FileVersionBlock.cs`

`FileVersionBlock` хранит ссылку на физические block-данные:

- `Sequence`
- `BlockStorageKey`
- `LengthBytes`
- `StoredSizeBytes`

Смысл:

- один `FileVersion` раскладывается на упорядоченный список блоков;
- `Sequence` восстанавливает исходный порядок байтов;
- `BlockStorageKey` связывает метаданные с физическим block storage.

## 4. Как данные попадают в block storage

### 4.1 Кто отвечает за запись block-данных

За это отвечает `IFileContentStore`.

Реальная реализация:

- `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`

Именно этот класс:

- читает файл;
- режет на блоки;
- вычисляет block key;
- сохраняет блоки;
- возвращает DTO с метаданными блоков обратно в snapshot repository.

### 4.2 Где вызывается StoreFileAsync

В `EfRepositorySnapshotRepository.SaveSnapshotAsync(...)` для изменившихся или новых файлов вызывается `IFileContentStore.StoreFileAsync(absolutePath)`.

Далее returned payload маппится в `FileVersionBlock`.

## 5. C# -> P/Invoke -> Rust flow

Это один из главных технических моментов проекта.

### 5.1 Граница C# и Rust

Граница проходит через:

- `src/Veyra.Infrastructure.Native/Interop/VeyraCoreNative.cs`
- `native/veyra_core/veyra_core.h`
- `native/veyra_core/src/ffi/api.rs`

### 5.2 Как выглядит путь при native store

1. C# вызывает `RustFileContentStore.StoreFileAsync(...)`.
2. `RustFileContentStore` готовит параметры.
3. Через `VeyraCoreNative` вызывается native функция.
4. В Rust это приходит в `native/veyra_core/src/ffi/api.rs`.
5. Дальше вызывается логика `native/veyra_core/src/block_store/mod.rs`.
6. Rust возвращает JSON payload с block metadata.
7. C# десериализует результат и сохраняет метаданные в SQLite.

### 5.3 Как выглядит путь при native restore

1. UI запускает `RestoreFileVersionCommand`.
2. `src/Veyra.Application/Commands/Repository/RestoreFileVersionHandler.cs` находит нужную version и блоки.
3. Handler вызывает `IFileContentStore.RestoreFileAsync(...)`.
4. `RustFileContentStore.RestoreFileAsync(...)` сортирует блоки по `Sequence`.
5. Если это native-compatible path, вызывается `VeyraCoreNative.RestoreFileBlocks(...)`.
6. Rust-функция в `ffi/api.rs` вызывает `block_store::restore_file_from_blocks(...)`.
7. Rust собирает временный файл, затем заменяет target.
8. C# после restore дополнительно валидирует полный `expectedContentHash`, если он передан.

## 6. Что такое дедупликация в этом проекте

Под дедупликацией в VeyraFlow реально понимается не хранение множества полных копий файла, а хранение блока только один раз с повторным использованием уже существующего блока по ключу.

Это подтверждается:

- `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`
- `native/veyra_core/src/block_store/mod.rs`
- `src/Veyra.Domain/Entities/FileVersioning/FileVersionBlock.cs`

## 7. Почему используется блочная дедупликация

Причина видна из архитектуры сущностей:

- `FileVersion` не хранит blob всего файла;
- он хранит коллекцию `FileVersionBlock`;
- одинаковые куски данных между версиями и даже между разными файлами могут ссылаться на один физический блок.

Что было бы хуже без этого:

- каждая правка в большом файле сохранялась бы как полная копия;
- объём локального хранилища рос бы быстрее;
- cloud upload тоже был бы дороже по времени и размеру.

## 8. Как реально режется файл на блоки

### 8.1 Размер блока

Подтверждено в коде:

- `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`: `DefaultChunkSize = 64 * 1024`
- `src/Veyra.Infrastructure.Native/Interop/VeyraCoreNative.cs`: fallback к `64 * 1024`
- `native/veyra_core/src/ffi/api.rs`: default `64 * 1024`

Реальный размер блока по inspected коду: `64 KiB`.

### 8.2 Фиксированный или content-defined

По inspected реализации это фиксированный размер блока.

Подтверждение:

- в `RustFileContentStore` файл читается фиксированными чанками;
- в `native/veyra_core/src/block_store/mod.rs` хеш считается по текущему `chunk`;
- признаков content-defined chunking по rolling hash или Rabin fingerprint в найденном коде нет.

На защите нужно говорить именно так:

- `частично оптимизировано за счет фиксированных 64 KiB блоков`;
- `content-defined chunking в inspected версии не найден`.

## 9. Какие хеши используются и зачем

### 9.1 Whole-file hash

Для версии файла в доменной модели используется `ContentHashSha256`:

- `src/Veyra.Domain/Entities/FileVersioning/FileVersion.cs`
- `src/Veyra.Infrastructure.Native/Scanning/RustRepositoryScanner.cs`
- `src/Veyra.Application/Commands/Repository/RestoreFileVersionHandler.cs`

Этот hash нужен для:

- идентификации содержимого file version;
- проверки итогового файла после restore;
- summary и diff preview.

### 9.2 Block key в native path

Native branch использует BLAKE3:

- `native/veyra_core/src/block_store/mod.rs`
- `let hash = blake3::hash(chunk).to_hex().to_string();`

Этот hash используется как block key и как часть пути хранения.

### 9.3 Block key в managed encrypted path

Когда включена защита артефактов, используется managed storage branch:

- `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`
- `ManagedHashPrefix = "sha256-"`

Тогда ключ блока строится из SHA-256 stored payload.

### 9.4 Почему в проекте встречаются и SHA-256, и BLAKE3

Это не ошибка архитектуры, а два разных уровня:

- SHA-256 используется как whole-file content hash и как ключ managed encrypted block payload;
- BLAKE3 используется в native block storage для быстрого ключа чанка.

На защите корректная формулировка:

- `SHA-256 — контроль целостности версии файла и часть managed storage branch`;
- `BLAKE3 — быстрый hash для native block-level dedup`.

## 10. Где физически лежат блоки

### 10.1 Native block storage

Физический путь строится в `native/veyra_core/src/block_store/mod.rs`.

Формат:

- `store_root/blocks/aa/bb/<hash>.zst`

То есть используется разбиение по первым байтам хеша для уменьшения размера одного каталога.

### 10.2 Managed encrypted block storage

Физический путь строится в `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`.

Формат:

- `%blockStore%/managed/blocks/aa/bb/hash.bin`

### 10.3 Что хранится в SQLite, а что на диске

В SQLite:

- snapshot metadata;
- file graph;
- `FileIdentity`;
- `FileVersion`;
- `FileVersionBlock`;
- text diff cache;
- sync queue;
- journal;
- watched setup.

На файловом storage:

- сами блоки файлов;
- временные preview-артефакты;
- архивные snapshot bundles, если включен archive-mode;
- временные restored файлы для compare/preview.

## 11. Сжимается ли блок

### 11.1 Native ветка

Да, в native ветке используется zstd:

- `native/veyra_core/src/block_store/mod.rs`
- `native/veyra_core/src/crypto/mod.rs`

При этом код показывает важную деталь:

- payload пакуется в envelope с magic `VYB1`;
- zstd применяется только если сжатый вариант меньше;
- legacy native blocks могли храниться как bare zstd stream без envelope.

### 11.2 Managed encrypted ветка

В managed ветке используется Brotli, а не zstd:

- `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`
- `ManagedCompressionBrotli = 1`

Это важно не перепутать на защите:

- `zstd` подтвержден для native block store;
- `Brotli` используется в managed encrypted payload branch.

## 12. Шифруется ли блок

Да, но не во всех ветках одинаково.

### 12.1 Где используется AES-GCM

`src/Veyra.Infrastructure.Native/Security/Encryption/ArtifactBlockCryptor.cs`

Подтверждено:

- `AesGcm`
- nonce 12 bytes
- tag 16 bytes
- envelope magic `VYRAENC`

### 12.2 Что именно шифруется

Шифруются block payload-ы в managed artifact encryption branch.

Ключевые моменты:

- plaintext блока сначала может быть упакован в managed envelope `VYRBLK`;
- затем этот payload защищается `ArtifactBlockCryptor.Protect(...)`;
- итоговый stored payload получает SHA-256 based key с префиксом `sha256-`.

### 12.3 Шифруются ли metadata

Локальные SQLite metadata целиком не шифруются.

Отдельно существует защита cloud metadata:

- `src/Veyra.Infrastructure.Native/Security/MetadataProtection/CloudMetadataProtectionService.cs`

Но это не означает полного шифрования всей локальной БД.

## 13. Как система понимает, что блок уже существует

Логика одинаковая по смыслу в обеих ветках:

1. Для чанка вычисляется ключ.
2. Строится физический путь.
3. Проверяется наличие уже существующего block file.
4. Если block file уже есть, новый физический блок не записывается.
5. В `FileVersionBlock` всё равно создается ссылка на уже существующий block key.

Следствие:

- одинаковые блоки повторно не дублируются;
- это работает как между двумя версиями одного файла, так и между разными файлами.

## 14. Как сохраняется порядок блоков

Порядок сохраняется полем `Sequence`:

- `src/Veyra.Domain/Entities/FileVersioning/FileVersionBlock.cs`
- индекс `(FileVersionId, Sequence)` задан в `src/Veyra.Infrastructure.Data/Persistence/Context/VeyraDbContext.cs`

Почему это важно:

- физические block file не хранят позицию внутри файла сами по себе;
- исходный файл восстанавливается именно по упорядоченному списку блоков.

## 15. Как считается compression ratio

В inspected коде не найдено отдельного persisted поля `CompressionRatio`.

Но для каждого блока сохраняются:

- `LengthBytes`
- `StoredSizeBytes`

То есть коэффициент можно вычислить как производную метрику:

- `StoredSizeBytes / LengthBytes`

Корректная формулировка:

- `в inspected версии ratio не хранится отдельным столбцом, но может быть вычислен по метаданным блока`.

## 16. Что происходит при удалении snapshot

Snapshot lifecycle и cleanup затрагивают не только таблицу snapshot, но и graph references.

Ключевые места:

- `src/Veyra.Infrastructure.Data/Setup/Retention/EfRepositoryRetentionService.cs`
- `src/Veyra.Infrastructure.Data/Setup/Archiving/RepositorySnapshotArchiveService.cs`

Реальное поведение:

1. Сначала snapshot помечается soft-delete или архивируется.
2. Затем выполняется sweep зависимых ссылок.
3. После этого выполняется удаление неиспользуемых block files.
4. Блок можно физически удалить только если больше ни одна версия на него не ссылается.

Это подтверждается тестами:

- `tests/Veyra.Infrastructure.Data.Tests/RetentionPolicyQualityGateTests.cs`
- сценарий `RunRetention_Apply_KeepsBlockFile_WhenRetainedSnapshotStillReferencesIt`

## 17. Когда блок можно физически удалить

Только когда он стал unreferenced.

Практический смысл:

- наличие soft-deleted snapshot само по себе не означает, что block file уже безопасно удалить;
- сначала нужно убедиться, что другой `FileVersion` на него не ссылается.

## 18. Что будет при hash collision

Отдельной специальной collision-resolution логики в inspected коде не найдено.

Практически система опирается на криптографические свойства:

- SHA-256 для file version;
- BLAKE3 или SHA-256 для block keys.

Корректная защита формулировки:

- `вероятность коллизии принята как пренебрежимо малая`;
- `специального ручного разрешения коллизий по inspected коду не реализовано`.

## 19. Что будет при повреждении блока

### 19.1 Restore path

Если block file поврежден:

- native restore может завершиться ошибкой decode/decompress;
- managed restore может не распаковать/не расшифровать payload;
- итоговый `expectedContentHash` может не совпасть.

### 19.2 Integrity path

Есть отдельная служба:

- `src/Veyra.Infrastructure.Data/Setup/Integrity/EfRepositoryIntegrityService.cs`

Но важное ограничение:

- managed `sha256-*` блоки реально пересчитываются;
- для native BLAKE3 blocks по inspected коду в этой службе нет симметричной повторной проверки payload по содержимому, только existence-style verification path.

Это нужно выносить в `Critical findings`, а не скрывать.

## 20. Как проверяется целостность после восстановления

В `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs` после записи target-файла вызывается проверка полного файла:

- если задан `expectedContentHash`, выполняется `ValidateRestoredFileHashAsync(...)`;
- внутри используется `SHA256.Create()` и сравнение с ожидаемым `ContentHashSha256`.

Это означает:

- restore завершается не просто по факту записи байтов;
- дополнительно валидируется, что собранный файл соответствует ожидаемой file version.

## 21. Полный restore flow

### 21.1 Откуда пользователь выбирает версию

Реальный пользовательский restore инициируется из:

- `src/Veyra.Desktop/ViewModels/Pages/Explorer/RepositoryExplorerViewModel.cs`

UI показывает историю версий и отправляет `RestoreFileVersionCommand`.

### 21.2 Какой handler выполняет restore

`src/Veyra.Application/Commands/Repository/RestoreFileVersionHandler.cs`

Handler:

1. Находит version и block list.
2. Вычисляет target path.
3. Учитывает `OverwriteCurrent`.
4. Вызывает `IFileContentStore.RestoreFileAsync(blocks, targetPath, overwrite, expectedHash, ct)`.

### 21.3 Почему блоки сортируются по Sequence

Потому что блоки в БД — это не один blob, а сегменты файла.

Без `Sequence` нельзя было бы гарантировать исходный порядок байтов.

Реальный код сортировки подтвержден в:

- `src/Veyra.Infrastructure.Native/Storage/RustFileContentStore.cs`
- `blocks.OrderBy(b => b.Sequence)`

### 21.4 Что означает overwrite existing

Если `overwriteExisting = false`:

- при существующем target-файле restore должен завершиться ошибкой;

если `overwriteExisting = true`:

- текущий файл в target path может быть заменен восстановленной версией.

Это разделяет два сценария:

- restore в новый файл;
- restore с заменой текущего.

### 21.5 Как из списка блоков получается исходный файл

Простой язык для защиты:

1. Для версии файла система берет список блоков.
2. Блоки сортируются по порядковому номеру `Sequence`.
3. Каждый блок читается из block storage.
4. Если блок был сжат, он распаковывается.
5. Если блок был зашифрован, он расшифровывается.
6. Полученные байты последовательно записываются в выходной файл.
7. После записи всех блоков считается SHA-256 итогового файла.
8. Если hash совпал, восстановление считается корректным.

Именно поэтому изображение, документ, архив или любой другой бинарный файл восстанавливается одинаковым базовым механизмом: система не “рисует” файл заново, а собирает исходный поток байтов по блокам.

## 22. Восстановление изображений

### 22.1 Хранятся ли PNG/JPG/WebP/TIFF как перекодированные данные

По inspected storage pipeline — нет.

Найденный код показывает generic byte-stream storage:

- `RustFileContentStore` читает исходный файл как байты;
- делит его на блоки;
- при restore собирает обратно исходные байты.

Следствие:

- PNG/JPG/WebP/TIFF и другие форматы не перекодируются при сохранении;
- EXIF/ICC/metadata сохраняются, если restore прошел byte-to-byte;
- JPEG не теряет качество повторно только из-за хранения, потому что система не делает повторный JPEG encode.

### 22.2 Где image-specific логика все же есть

Image-specific логика применяется не в storage, а в preview/diff:

- `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`
- `src/Veyra.Infrastructure.Data/Setup/Preview/ImageDiffOverlayBuilder.cs`
- `native/veyra_core/src/image_diff/mod.rs`
- `src/Veyra.Desktop/Services/Preview/InteractiveImageDiff/InteractiveImageDiffRenderer.cs`

То есть:

- хранение и restore — generic block storage;
- image preview и image diff — отдельный слой анализа поверх восстановленных версий.

## 23. Что ставится в sync queue после snapshot

После успешного `ScanRepositoryCommand` с созданием versioned snapshot выполняется:

- `IRepositoryCloudSyncOrchestrator.TryPushLatestSnapshotAsync(repositoryId)`

Подтверждение:

- `src/Veyra.Application/Commands/Repository/ScanRepositoryHandler.cs`
- `src/Veyra.Infrastructure.Data/Sync/RepositoryCloudSyncOrchestrator.cs`

Таким образом, локальный snapshot сначала фиксируется в SQLite и local block storage, а потом помещается в queue на cloud push.

## 24. Ошибки и логирование

В snapshot/restore pipeline участвуют:

- `src/Veyra.Application/Common/Behaviors/LoggingBehavior.cs`
- `src/Veyra.Application/Common/Behaviors/OperationJournalBehavior.cs`
- `src/Veyra.Application/Common/Behaviors/ValidationBehavior.cs`
- `src/Veyra.Domain/Entities/OperationJournalEntry.cs`

Что это дает:

- ошибки validation отсекаются до infrastructure;
- операции попадают в journal;
- handler-level и infrastructure-level исключения логируются структурированно.

Отдельно в `EfRepositorySnapshotRepository` предусмотрены non-fatal ветки для busy files: snapshot может завершиться с предупреждениями, а не падать полностью.

## 25. Ограничения и честные формулировки для защиты

### Что можно утверждать уверенно

- В проекте реально есть block-level deduplication.
- Файлы реально раскладываются на фиксированные 64 KiB блоки.
- В native storage реально используется BLAKE3 и zstd.
- В managed encrypted branch реально используется AES-GCM и Brotli.
- Restore реально собирает файл обратно по `Sequence`.
- После restore реально проверяется итоговый SHA-256 файла.

### Что нужно формулировать аккуратно

- Не говорить, что всегда используется один и тот же block key algorithm: в коде есть native BLAKE3 branch и managed SHA-256 branch.
- Не говорить, что вся локальная БД зашифрована: это не подтверждается кодом.
- Не говорить, что целостность native blocks везде повторно перепроверяется так же строго, как managed `sha256-*` блоков: это не подтверждено.

## 26. Ключевой вывод для защиты

Storage subsystem в VeyraFlow — это не простое копирование файлов в папку резервных копий. По реальному коду система:

- создает snapshot состояния репозитория;
- связывает snapshot с `FileIdentity` и `FileVersion`;
- хранит содержимое через `FileVersionBlock`;
- повторно использует существующие блоки за счет дедупликации;
- умеет восстанавливать исходный файл путем последовательной сборки блоков;
- валидирует итоговый hash после restore.

Именно эта цепочка делает проект системой контроля состояния файлов и истории версий, а не просто “папкой с копиями”.
