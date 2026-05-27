# Localization — VeyraFlow

## Where translations live

User-facing strings are stored in:

- `src/Veyra.Desktop/Localization/en.json`
- `src/Veyra.Desktop/Localization/ru.json`

Keys must match in both files. When a new key is added, add its value for both RU and EN at the same time.

## How to use strings in the UI

In XAML use the current mechanism:

```xml
Text="{loc:Tr some.key}"
Content="{loc:Tr some.button}"
ToolTip.Tip="{loc:Tr some.tooltip}"
```

In C# / ViewModel use:

```csharp
Loc.T("some.key")
Loc.F("some.format", value)
Loc.P("some.plural", count)
```

Do not surface raw enum or status values directly to the user. For statuses, use an explicit mapping:

```
internal_status -> localization key -> translated text
```

If an unknown status arrives, show a safe fallback via `common.unknown_value` rather than the raw technical token.

## Office Mode and Professional Mode

The interface mode is determined by `UserExperienceManager`.

- Office/basic mode should use plain language: "Old version cleanup rules", "State snapshot", "Needs attention".
- Professional mode may use technical terms: "Retention policy", "Snapshot", "Cloud sync", "Block storage".

If the same screen requires different wording, add separate keys, for example:

- `repo_settings.retention_policy_basic`
- `repo_settings.retention_policy`

Select the key in the ViewModel based on the current mode, as already done for sync/retention statuses.

## Placeholders

The RU and EN versions of the same string must have identical placeholders:

```json
"example.count": "{0} files, {1} versions"
```

Do not change the number of `{0}`, `{1}` placeholders between languages. After adding keys, validate the JSON for:

- the same set of keys in both files
- identical placeholders
- no raw enum/status values in UI strings

## Common risk areas

Pay special attention to:

- Cloud Repository Manager and Cloud Information Window
- Background operations
- Repository cards
- Snapshot history and rollback/restore dialogs
- Version comparison window
- Folder filters
- Sync/retention/cleanup statuses

If a string is visible to the user, it must not be hard-coded in XAML or a ViewModel.
