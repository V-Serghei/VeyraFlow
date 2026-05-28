# Defense docs по VeyraFlow

Этот каталог содержит пакет документов для защиты дипломного проекта по `VeyraFlow`. Материалы опираются прежде всего на аудит реального кода `VeyraFlow.sln`, а не только на существующие `README` и старые `docs`.

## Как читать пакет

Если нужен общий технический разбор, начинайте с:

- [FULL_SYSTEM_ANALYSIS.md](./FULL_SYSTEM_ANALYSIS.md)

Если нужен глубокий разбор хранилища и восстановления:

- [STORAGE_DEDUP_RESTORE_DEEP_DIVE.md](./STORAGE_DEDUP_RESTORE_DEEP_DIVE.md)

Если нужен разбор форматов, diff и презентационных рисков по поддержке файлов:

- [DIFF_FORMATS_DEEP_DIVE.md](./DIFF_FORMATS_DEEP_DIVE.md)

Если нужен cloud/offline/security блок:

- [CLOUD_OFFLINE_SECURITY_DEEP_DIVE.md](./CLOUD_OFFLINE_SECURITY_DEEP_DIVE.md)

Если нужна речь для выступления:

- [PRESENTATION_DEFENSE_SCRIPT.md](./PRESENTATION_DEFENSE_SCRIPT.md)

Если нужна шпаргалка по вопросам комиссии:

- [COMMISSION_QA.md](./COMMISSION_QA.md)

Если нужно проверить презентацию по слайдам:

- [PRESENTATION_AUDIT_AND_RECOMMENDATIONS.md](./PRESENTATION_AUDIT_AND_RECOMMENDATIONS.md)

Если нужен честный roadmap и список ограничений:

- [ROADMAP_AND_LIMITATIONS.md](./ROADMAP_AND_LIMITATIONS.md)

## Назначение файлов

- `FULL_SYSTEM_ANALYSIS.md` — полная карта solution, слоев, зависимостей, DI, паттернов, UI, БД, background services и тестов.
- `STORAGE_DEDUP_RESTORE_DEEP_DIVE.md` — snapshot pipeline, block storage, дедупликация, сжатие, шифрование, restore и image restore.
- `DIFF_FORMATS_DEEP_DIVE.md` — text diff, Word OOXML, image diff, audio diff, generic binary и таблица поддержки форматов.
- `CLOUD_OFFLINE_SECURITY_DEEP_DIVE.md` — REST cloud sync, Go API, PostgreSQL, queue, offline-first, guest mode и security risks.
- `PRESENTATION_DEFENSE_SCRIPT.md` — elevator pitch, сценарий на 7 и 12 минут, переходы между слайдами и безопасные формулировки.
- `COMMISSION_QA.md` — набор вопросов и ответов для устной защиты.
- `PRESENTATION_AUDIT_AND_RECOMMENDATIONS.md` — сопоставление презентации с кодом, ошибки позиционирования и рекомендации по правкам.
- `ROADMAP_AND_LIMITATIONS.md` — что уже готово, что частично реализовано, что лучше не overclaim и что развивать дальше.

## Важная оговорка

Во всех документах используется одна и та же честная позиция:

- `VeyraFlow` — это не замена Windows Explorer.
- Корректное описание проекта: desktop-приложение для отслеживания состояния выбранных файлов и папок, создания истории версий, snapshots, сравнения версий и восстановления файлов.
- Если функциональность подтверждена кодом, рядом указан путь к файлу.
- Если реализация найдена частично, она помечена как `частично реализовано`.
- Если claim присутствует в презентации, но не подтвержден inspected кодом, он помечен как `заявлено, но требует доработки`.
