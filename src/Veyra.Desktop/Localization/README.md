# Localization dictionaries

This folder stores UI translations as key-value tables.

## Current languages

- `en.json` - English base dictionary
- `ru.json` - Russian base dictionary

## Supported dictionary formats

- Single-file language dictionary: `en.json`, `ru.json`, `de.json`
- Modular dictionary files: `<lang>.<module>.json` (for example `en.explorer.json`, `ru.settings.json`)

All files with the same `<lang>` prefix are merged at startup.

## Add a new language

1. Copy `en.json` to a new file named by language code, for example `de.json`.
2. Translate values, keep keys unchanged.
3. Optionally split by modules (`de.explorer.json`, `de.settings.json`, ...).
4. Rebuild/run app. Language is discovered automatically from `Localization/*.json`.
5. Open **Settings -> General -> Application language** and select the new language.

## Rules

- Keys are stable IDs, for example: `app_settings.title`.
- If a key is missing in selected language, app falls back to English.
- Duplicate keys across module files are allowed but last file wins. Use Settings diagnostics to detect duplicates.
- Keep placeholders intact in formatted strings (`{0}`, `{1}`, ...).

## Plural forms

Use key groups by plural category:

- `key.one`
- `key.few`
- `key.many`
- `key.other`

Usage in code:

- `Loc.P("setup.directories_selected", count, count)`

## Where keys are used

- XAML: `{loc:Tr app_settings.title}`
- ViewModels: `Loc.T("app_settings.title")`, `Loc.F("key.with.format", arg0)`, `Loc.P("key.base", count, args...)`

## Health check

Run localization validation:

- `powershell -NoProfile -ExecutionPolicy Bypass -File tools/localization-health.ps1`
- `powershell -NoProfile -ExecutionPolicy Bypass -File tools/localization-health.ps1 -FailOnIssues` (CI-safe mode)

