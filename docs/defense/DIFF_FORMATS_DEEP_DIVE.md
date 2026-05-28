# Diff, preview и поддерживаемые форматы: deep dive

## Основание для выводов

Ключевые файлы, по которым проверялась реальная поддержка форматов:

- `src/Veyra.Application/Common/Files/KnownFileExtensions.cs`
- `src/Veyra.Application/Queries/Repository/GetTextDiffHandler.cs`
- `src/Veyra.Application/Queries/Repository/GetFileVersionDiffPreviewHandler.cs`
- `src/Veyra.Application/Queries/Repository/GetPendingFileDiffPreviewHandler.cs`
- `src/Veyra.Application/Services/ManagedTextDiffEngine.cs`
- `src/Veyra.Application/Services/Diff/WordSemanticProjection.cs`
- `src/Veyra.Infrastructure.Native/Diffing/RustTextDiffEngine.cs`
- `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`
- `src/Veyra.Infrastructure.Data/Preview/Archives/ArchiveDiffPreviewBuilder.cs`
- `src/Veyra.Infrastructure.Data/Preview/Audio/AudioDiffPreviewBuilder.cs`
- `src/Veyra.Infrastructure.Data/Preview/Svg/SvgStructuralDiffAnalyzer.cs`
- `src/Veyra.Infrastructure.Data/Setup/Preview/ImageDiffOverlayBuilder.cs`
- `native/veyra_core/src/image_diff/mod.rs`
- `native/veyra_core/src/text_diff/mod.rs`
- `src/Veyra.Desktop/ViewModels/Windows/Compare/FileVersionCompareWindowViewModel.cs`
- `src/Veyra.Desktop/Services/Preview/InteractiveImageDiff/InteractiveImageDiffRenderer.cs`
- `src/Veyra.Desktop/Services/Preview/WordCompare/NativeWordCompareService.cs`

## 1. Как выбирается режим diff

Центральная точка маршрутизации preview/diff:

- `src/Veyra.Infrastructure.Data/Setup/Snapshots/EfRepositorySnapshotRepository.cs`
- методы `GetPendingFileDiffPreviewAsync(...)` и `GetFileVersionDiffPreviewAsync(...)`

Практический роутинг по inspected коду:

1. Если расширение входит в text diff formats, строится text diff.
2. Если это `.zip`, строится archive diff.
3. Если это image diff format, строится image preview/diff.
4. Если это audio diff format, строится audio preview/diff.
5. Во всех остальных случаях возвращается generic binary summary.

Это важно для защиты: поддержка форматов в VeyraFlow неоднородна. Нельзя говорить, что все форматы имеют одинаково глубокий semantic diff.

## 2. Text diff

### 2.1 Где реализован

Основные файлы:

- `src/Veyra.Application/Queries/Repository/GetTextDiffHandler.cs`
- `src/Veyra.Application/Services/ManagedTextDiffEngine.cs`
- `src/Veyra.Infrastructure.Native/Diffing/RustTextDiffEngine.cs`
- `native/veyra_core/src/text_diff/mod.rs`

### 2.2 Какие форматы реально идут в text diff

Подтверждено в `src/Veyra.Application/Common/Files/KnownFileExtensions.cs`.

К специализированному text diff относятся:

- plain text и config: `txt`, `md`, `json`, `xml`, `yaml`, `yml`, `ini`, `toml`, `log`
- код: `cs`, `js`, `ts`, `tsx`, `jsx`, `java`, `py`, `go`, `rs`, `cpp`, `h`
- markup/UI text: `xaml`, `axaml`
- таблицы в текстовом виде: `csv`
- web/text-like: `html`, `css`, `scss`

### 2.3 Как выполняется text diff

Реальная цепочка:

1. Две версии файла при необходимости восстанавливаются во временные файлы через `IFileContentStore.RestoreFileAsync(...)`.
2. Содержимое читается как текст.
3. Используется `ITextDiffEngine`.
4. Если доступен native path, работает `RustTextDiffEngine`.
5. Fallback-реализация — `ManagedTextDiffEngine`.

### 2.4 Какой уровень diff используется

По inspected коду основной уровень — line-level diff с hunk-структурой.

Подтверждается сущностями:

- `src/Veyra.Domain/Entities/TextDiff/FileVersionTextDiff.cs`
- `src/Veyra.Domain/Entities/TextDiff/FileVersionTextDiffHunk.cs`
- `src/Veyra.Domain/Entities/TextDiff/FileVersionTextDiffLine.cs`
- `src/Veyra.Domain/Entities/TextDiff/TextLineAtom.cs`

Практически это означает:

- есть line-level diff;
- есть grouping в hunks;
- есть line atom storage для повторного использования строк;
- полноценного AST diff для кода по inspected версии не найдено.

### 2.5 Где diff кэшируется

Text diff может сохраняться в SQLite:

- `FileVersionTextDiff`
- `FileVersionTextDiffHunk`
- `FileVersionTextDiffLine`
- `TextLineAtom`

Индексы подтверждены в `src/Veyra.Infrastructure.Data/Persistence/Context/VeyraDbContext.cs`.

### 2.6 Ограничения

- большие файлы могут быть `IsTruncated`;
- точной поддержки всех экзотических кодировок в inspected коде не заявлено;
- structured diff для JSON/XML/YAML как отдельный режим не найден, даже если эти форматы идут в text diff.

## 3. Word OOXML / semantic diff

### 3.1 Что реально реализовано

По inspected коду Word OOXML path реализован частично.

Основной файл:

- `src/Veyra.Application/Services/Diff/WordSemanticProjection.cs`

Поддерживаемые расширения в этом модуле:

- `.docx`
- `.docm`
- `.dotx`
- `.dotm`

### 3.2 Как это работает

`WordSemanticProjection`:

- открывает OOXML archive;
- читает `word/document.xml`;
- при наличии читает `word/styles.xml`;
- строит semantic lines по абзацам и run-ам;
- нормализует style tokens;
- старается сгладить ложные различия, когда Word дробит одинаковый видимый текст на много run-ов.

### 3.3 Что сравнивается

Сравниваются:

- paragraph-level structure;
- run text;
- часть style/token metadata.

### 3.4 Что НЕ подтверждено как полностью реализованное

По inspected коду не найден полноценный Word visual diff engine со своей моделью:

- без полноценного layout compare;
- без детального document-semantic tree diff по всем OOXML частям;
- без доказанной полной поддержки footnotes, comments, tracked changes, headers/footers как отдельного semantic слоя.

Корректная формулировка:

- `частично реализовано`;
- `семантическая проекция для OOXML есть, но это не полноценный deep semantic diff всего документа Word`.

### 3.5 Дополнительная Word-логика в Desktop

Есть отдельный Windows-specific путь:

- `src/Veyra.Desktop/Services/Preview/WordCompare/NativeWordCompareService.cs`

Это не внутренний semantic diff engine. Этот сервис:

- проверяет наличие Microsoft Word;
- через COM запускает `CompareDocuments`;
- открывает compare в самом Word.

Корректная защита:

- встроенная логика VeyraFlow для Word частичная;
- при наличии Microsoft Word на Windows можно открыть нативное compare Word как внешний инструмент.

### 3.6 Как правильно описывать на слайдах

Нельзя писать:

- `полноценный semantic diff Word документов реализован полностью`

Нужно писать:

- `для OOXML реализована семантическая текстово-структурная проекция; на Windows доступен запуск нативного сравнения в Microsoft Word`

### 3.7 План доработки

Если развивать дальше, реалистичный план такой:

1. Выделить semantic model для paragraphs, tables, runs, comments, footnotes.
2. Сравнивать не только текст, но и document parts.
3. Отдельно нормализовать numbering, styles, revision marks.
4. Добавить UI, который показывает paragraph-level changes внутри приложения.

Подходящие библиотеки:

- `DocumentFormat.OpenXml` для C#
- при необходимости `OpenXmlPowerTools`

## 4. Image diff

### 4.1 Где реализован

Ключевые файлы:

- `native/veyra_core/src/image_diff/mod.rs`
- `src/Veyra.Infrastructure.Native/NativeImageDiffInterop.cs`
- `src/Veyra.Infrastructure.Native/Interop/VeyraCoreNative.cs`
- `src/Veyra.Infrastructure.Data/Setup/Preview/ImageDiffOverlayBuilder.cs`
- `src/Veyra.Desktop/Services/Preview/InteractiveImageDiff/InteractiveImageDiffRenderer.cs`
- `src/Veyra.Desktop/Services/Preview/InteractiveImageDiff/InteractiveImageViewportController.cs`

### 4.2 Какие форматы реально поддерживаются

В `KnownFileExtensions.ImageDiffFormats` указаны:

- `png`
- `jpg`
- `jpeg`
- `bmp`
- `gif`
- `webp`
- `tif`
- `tiff`
- `svg`

Практически:

- raster image decode идет через Rust image stack;
- для SVG отдельно используется `resvg`;
- для animated GIF по inspected коду не найден специализированный multi-frame analyzer, поэтому безопаснее формулировать: `GIF поддерживается как image input, но поведение для анимации требует отдельной верификации`.

### 4.3 Как выбираются две версии

Есть два сценария:

- pending preview: baseline snapshot vs текущий файл на диске;
- version compare: `FileVersion` vs `FileVersion`.

Оба routed через `EfRepositorySnapshotRepository`.

### 4.4 Как выполняется сравнение

По inspected коду используются:

- сравнение размеров;
- попиксельное сравнение rasterized image;
- вычисление changed regions;
- визуализация overlay/heatmap/split/composite;
- для SVG дополнительно structural diff по элементам и атрибутам.

### 4.5 Реальные метрики

Подтверждены:

- `ChangedPixelCount`
- `ChangedPixelRatio`
- `ChangedRegionCount`
- `SimilarityRatio`
- для SVG: `AddedElementCount`, `RemovedElementCount`, `ModifiedElementCount`, `ChangedAttributeCount`

### 4.6 Что означает sensitivityPercent

`sensitivityPercent` используется в image diff renderer как порог чувствительности.

Практический смысл:

- чем выше чувствительность, тем меньше визуальная разница нужна, чтобы пиксель считался изменённым;
- параметр передается в native interop и desktop renderer.

### 4.7 Режимы визуализации

Подтверждены в native и desktop коде:

- `overlay`
- `heatmap`
- `split`
- `composite`

UI-локализация этих режимов подтверждена в `src/Veyra.Desktop/Localization/ru.json`.

### 4.8 Bounding boxes и changed regions

В native image diff есть region extraction, а в UI предусмотрено отображение boxes/region markers через interactive preview stack.

### 4.9 Ограничения

- large images ограничены по `MAX_PIXELS = 24_000_000` в native коде;
- SVG требует rasterization, поэтому поведение зависит от корректности render path;
- animated GIF не подтвержден как full animation diff;
- очень большие TIFF/SVG могут быть тяжелыми по памяти.

## 5. Audio diff

### 5.1 Где реализован

- `src/Veyra.Infrastructure.Data/Preview/Audio/AudioDiffPreviewBuilder.cs`
- `src/Veyra.Desktop/Services/Preview/Audio/AudioPreviewPlaybackService.cs`
- `tests/Veyra.Infrastructure.Data.Tests/AudioDiffPreviewBuilderTests.cs`

### 5.2 Реальное состояние

Специализированный audio diff в коде есть.

Это важно: нельзя говорить, что “аудио вообще не анализируется”. Анализируется, но не для всех форматов с одинаковой надежностью.

### 5.3 Что именно строится

`AudioDiffPreviewBuilder` строит:

- difference audio `.wav`
- waveform preview
- spectrogram preview
- spectral delta preview
- channel metrics
- band metrics
- changed segments по времени

Метрики:

- duration
- sample rate
- channels
- peak amplitude
- RMS
- signal similarity
- spectral similarity
- changed time ratio
- stereo correlation

### 5.4 Какие форматы реально routed в audio diff

Подтверждено в `KnownFileExtensions.AudioDiffFormats`:

- `wav`
- `mp3`
- `aac`
- `m4a`
- `wma`
- `aif`
- `aiff`

### 5.5 Что с flac и ogg

Это важное несоответствие презентации.

`flac` и `ogg`:

- перечислены в tracked formats;
- но не входят в `AudioDiffFormats`;
- значит по текущему inspected коду идут в generic binary mode, а не в специализированный audio diff.

Корректная формулировка:

- `заявлены как отслеживаемые форматы хранения`;
- `специализированный audio diff для них по текущему роутингу не подтвержден`.

### 5.6 Библиотеки

По inspected коду используется `NAudio`.

Ограничение:

- декодирование зависит от того, что реально умеет `AudioFileReader` и underlying platform stack;
- поэтому универсальную гарантию для всех контейнеров и кодеков давать нельзя.

### 5.7 Что говорить на защите

Лучше говорить:

- `для части аудиоформатов реализован waveform/spectrogram based preview-diff`;
- `поддержка зависит от декодирования NAudio, поэтому она частично реализована и требует дальнейшей нормализации по кодекам`.

## 6. Archive diff

### 6.1 Что реально есть

Специализированный archive diff подтвержден только для ZIP:

- `src/Veyra.Infrastructure.Data/Preview/Archives/ArchiveDiffPreviewBuilder.cs`
- `KnownFileExtensions.ArchiveDiffFormats` содержит только `.zip`

### 6.2 Что сравнивается

Archive diff строит:

- baseline/current entry count;
- added/removed/changed/unchanged entries;
- список entry differences.

### 6.3 Что с 7z/rar/tar/gz

Эти расширения есть в tracked lists, но по inspected коду не routed в specialized archive diff.

Корректная формулировка:

- `сохраняются и версионируются как binary`;
- `специализированный archive content diff пока подтвержден только для ZIP`.

## 7. Generic binary режим

### 7.1 Когда он используется

Когда формат:

- не попадает в text diff;
- не попадает в image diff;
- не попадает в audio diff;
- не попадает в zip archive diff;
- либо для пары файлов не удалось построить специализированный preview.

### 7.2 Что показывается пользователю

Подтверждено DTO:

- `src/Veyra.Application/DTOs/PendingChanges/PendingBinaryDiffSummaryDto.cs`

Показываются:

- размер baseline/current;
- `SizeDeltaBytes`;
- `BaselineHashSha256`;
- `CurrentHashSha256`;
- `ChunkSizeBytes`;
- количество блоков;
- `SharedBlockCount`;
- `DedupRatio`;
- `ChangedBlockRatio`;
- `ByteSimilarityRatio` если удалось посчитать.

### 7.3 Почему это полезно

Даже без semantic diff пользователь видит:

- что файл изменился;
- насколько сильно изменился по размеру и блочному overlap;
- есть ли высокая общность между версиями.

## 8. Таблица реальной поддержки форматов

| format | current support | storage mode | diff mode | preview mode | implementation file | risk / limitation | recommendation |
|---|---|---|---|---|---|---|---|
| `txt`, `md`, `log`, `ini`, `toml` | подтверждено | block storage + SQLite metadata | text diff | text preview | `KnownFileExtensions.cs`, `GetTextDiffHandler.cs` | большие файлы могут обрезаться | готово для слайдов |
| `csv` | подтверждено | block storage + SQLite metadata | text diff | text preview | `KnownFileExtensions.cs` | нет table-aware diff | можно быстро добавить table preview |
| `json`, `xml`, `yaml`, `yml` | подтверждено | block storage + SQLite metadata | text diff | text preview | `KnownFileExtensions.cs` | нет structured tree diff | хороший кандидат на post-defense structured diff |
| `cs`, `js`, `ts`, `tsx`, `jsx`, `java`, `py`, `go`, `rs`, `cpp`, `h` | подтверждено | block storage + SQLite metadata | text diff | text preview | `KnownFileExtensions.cs`, `ManagedTextDiffEngine.cs`, `RustTextDiffEngine.cs` | нет AST diff | достаточно для диплома |
| `docx`, `docm`, `dotx`, `dotm` | частично реализовано | block storage + SQLite metadata | partial Word semantic projection | text-like semantic projection, optional external Word compare | `WordSemanticProjection.cs`, `NativeWordCompareService.cs` | не полноценный semantic/visual diff | не заявлять как finished |
| `doc`, `rtf`, `odt` | частично реализовано | block storage + SQLite metadata | в основном generic binary | limited binary summary | `KnownFileExtensions.cs`, `EfRepositorySnapshotRepository.cs` | нет специализированного diff | не обещать semantic compare |
| `png`, `jpg`, `jpeg`, `bmp`, `webp`, `tif`, `tiff` | подтверждено | block storage + SQLite metadata | image diff | image preview + overlay | `image_diff/mod.rs`, `InteractiveImageDiffRenderer.cs` | большие файлы тяжелы по памяти | можно уверенно показывать |
| `svg` | подтверждено | block storage + SQLite metadata | image diff + structural SVG diff | vector preview via rasterized output | `SvgStructuralDiffAnalyzer.cs`, `image_diff/mod.rs` | complex SVG may render differently | сильная демонстрационная функция |
| `gif` | частично реализовано | block storage + SQLite metadata | image diff path есть | preview есть | `KnownFileExtensions.cs`, `image_diff/mod.rs` | animation-aware diff не подтвержден | формулировать аккуратно |
| `wav`, `mp3`, `aac`, `m4a`, `wma`, `aif`, `aiff` | частично реализовано | block storage + SQLite metadata | audio diff | waveform/spectrogram/difference audio | `AudioDiffPreviewBuilder.cs` | зависит от NAudio decoding | не обещать одинаковую надежность для всех кодеков |
| `flac`, `ogg` | отслеживание подтверждено, specialized diff не подтвержден | block storage + SQLite metadata | generic binary | binary summary | `KnownFileExtensions.cs` | в `AudioDiffFormats` не входят | исправить слайд поддержки |
| `zip` | подтверждено | block storage + SQLite metadata | archive diff | archive entry list preview | `ArchiveDiffPreviewBuilder.cs` | только ZIP | можно показывать как специализированный archive compare |
| `7z`, `rar`, `tar`, `gz` | частично реализовано | block storage + SQLite metadata | generic binary | binary summary | `KnownFileExtensions.cs` | content list diff не найден | не называть archive diff для всех архивов |
| `pdf` | отслеживание подтверждено | block storage + SQLite metadata | generic binary | binary summary | `KnownFileExtensions.cs` | встроенного PDF preview не найдено | хороший кандидат на preview-only |
| `xls`, `xlsx` | отслеживание подтверждено | block storage + SQLite metadata | generic binary | binary summary | `KnownFileExtensions.cs` | нет spreadsheet-aware preview | полезно добавить table preview |
| `ppt`, `pptx` | отслеживание подтверждено | block storage + SQLite metadata | generic binary | binary summary | `KnownFileExtensions.cs` | нет slide preview | не заявлять специализированный diff |
| `mp4`, `mkv`, `mov`, `avi`, `webm`, `wmv` | отслеживание подтверждено | block storage + SQLite metadata | generic binary | binary summary | `KnownFileExtensions.cs` | нет video preview/diff | можно добавить metadata preview |

## 9. Несоответствия между презентацией и кодом

### Подтверждено

- text diff для текстовых и кодовых форматов;
- image diff для основных raster formats и SVG;
- audio diff существует как отдельный модуль;
- generic binary mode существует.

### Требует исправления на слайдах

- `flac` и `ogg` нельзя показывать как подтвержденные specialized audio diff formats;
- `7z`, `rar`, `tar`, `gz` нельзя показывать как подтвержденный archive diff;
- `pptx`, `xlsx`, `pdf` нельзя показывать как formats с готовым specialized diff;
- Word OOXML лучше называть `частично реализованной семантической проекцией`, а не finished semantic diff.

## 10. Что несложно добавить

### Легко добавить

| формат / функция | почему легко | библиотека | что показать в UI | нужен ли полноценный diff | риск |
|---|---|---|---|---|---|
| `pdf` preview | нужен в основном render preview, не full diff | PDFium, Docnet, PdfPig | первая страница, page count, metadata | достаточно preview + metadata | умеренная нагрузка на render |
| `csv` table preview | формат уже text-like | встроенный parser / CsvHelper | таблица, строки, колонки | полноценный diff не обязателен | низкий |
| `json` structured diff | формат уже текстовый | `System.Text.Json` | tree view, changed keys | да, но без heavy UI | низкий |
| `xml` structured diff | структура естественно парсится | `XDocument` | node tree, changed attributes | желательно, но можно начать с preview | средний |
| `yaml` structured preview | формат уже поддержан как text | `YamlDotNet` | tree preview | можно начать без full structured diff | средний |
| `xlsx` sheet preview | high value и понятный UX | `ClosedXML` | sheet list, first rows | preview достаточно | средний |

### Средне по сложности

| формат / функция | почему средне | библиотека | что показать в UI | нужен ли полноценный diff | риск |
|---|---|---|---|---|---|
| `pptx` slide preview | нужно рендерить слайды или хотя бы структуру | Open XML + render bridge | slide list, text outline, preview thumbs | preview важнее full diff | средняя сложность рендера |
| `gif` frame preview | нужен multi-frame pipeline | ImageSharp / SkiaSharp | frame list, fps, first-frame diff | full animation diff не обязателен | память и время |
| `flac`, `ogg` waveform | нужно стабильное декодирование | FFmpeg, NAudio+plugins | waveform, duration, bitrate | full audio diff можно упростить | decoder dependency |
| `mp4`, `mkv` metadata preview | достаточно metadata layer | MediaInfo | duration, codec, resolution, audio tracks | full video diff не нужен | внешний runtime |
| `7z`, `rar`, `tar`, `gz` content list | нужен unified archive parser | SharpCompress | entry list, size, hashes | diff по content list | parser limitations |

### Сложно или нецелесообразно для диплома

| формат / функция | почему сложно | библиотека | что лучше делать | риск |
|---|---|---|---|---|
| full semantic `docx` diff | много OOXML parts и layout нюансов | Open XML + custom model | оставить как future work | высокий |
| full video visual diff | дорого по CPU/GPU и UX | FFmpeg/OpenCV | ограничиться metadata preview | высокий |
| universal audio diff for every codec | decoder matrix слишком велика | FFmpeg stack | сузить гарантируемый список форматов | высокий |
| semantic `pptx` diff | слайды, layout, media, notes | Open XML + renderer | сначала preview/outline diff | высокий |

## 11. Как корректно объяснять image diff, audio diff и generic binary

### Image diff

Лучше говорить:

- `для изображений выполняется специализированное сравнение, включая попиксельный анализ и визуализацию изменений`

### Audio diff

Лучше говорить:

- `для части аудиоформатов реализован preview-diff на основе waveform и спектральных метрик; универсальная поддержка всех кодеков не заявляется`

### Generic binary

Лучше говорить:

- `для остальных форматов система все равно хранит версии и умеет их восстанавливать, а для сравнения показывает binary-level summary, размер, hash и overlap блоков`

## 12. Ключевой вывод для защиты

Поддержка форматов в VeyraFlow реальна, но многоуровнева:

- текст и код имеют наиболее зрелый diff;
- изображения имеют сильный специализированный compare path;
- аудио поддерживается частично и зависит от декодирования;
- Word OOXML реализован как partial semantic projection, а не finished semantic platform;
- многие office, archive и media formats пока честнее относить к generic binary mode или preview-only backlog.

Именно такая честная формулировка делает защиту сильнее, потому что она совпадает с кодом.
