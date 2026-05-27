# Offline-First Behavior & Guest Mode

## Architecture Philosophy

Cloud is an **optional layer**, not a hard dependency. All local operations (snapshots, history, diffs, retention, cleanup) work independently of network and authentication state. The app is designed to start, run, and remain fully functional with no internet connection.

---

## Guest Mode

### Definition
Guest mode = `!HasActiveProfile` = no active user profile in the SQLite `user_profiles` table.

### What works in guest mode
| Feature | Status |
|---------|--------|
| Repository creation and management | ✅ Full |
| Manual and automatic snapshots | ✅ Full |
| File history and diff viewer | ✅ Full |
| Retention policy (local) | ✅ Full (Pro only in UI, but engine runs) |
| Transient cache cleanup | ✅ Full |
| Monitoring, operation journal | ✅ Full |
| Settings (general, theme, language) | ✅ Full |

### What is disabled in guest mode
| Feature | Behavior |
|---------|----------|
| Cloud sync buttons | Hidden (wrapped in `IsVisible="{Binding HasActiveProfile}"`) |
| Connectivity banner | Hidden (`ShowConnectivityBanner` requires `HasActiveProfile`) |
| Dashboard cloud panel | Hidden (`ShowConnectivityPanel` requires `HasCloudAccess`) |
| Sync tab cloud actions | Disabled via `CanSyncToCloud = false` |
| Per-repository sync now / repair | Disabled via `CanRunSyncNow = HasCloudAccess && ...` |

### What must NOT happen in guest mode
- Cloud warnings must not be shown
- "Server unavailable" messages must not appear
- Connectivity probing must be paused (no background HTTP requests to cloud endpoint)
- No cloud-related startup ops (gated by `!string.IsNullOrWhiteSpace(activeUsername)` in App.axaml.cs)

---

## Connectivity State Machine

**Service**: `ConnectivityStatusService` (singleton, `IConnectivityStatusService`)

**States** (`ConnectivityState` enum):
| State | Meaning |
|-------|---------|
| `Unknown` | Not yet probed, or probing disabled (guest mode) |
| `Online` | HEAD probe to cloud endpoint succeeded |
| `InternetUnavailable` | No network interface or DNS/socket failure |
| `CloudUnavailable` | Network OK but cloud endpoint unreachable / timeout |

**Probing behavior**:
- Polls every 90s (Online), 3min (Degraded), 45s (Unknown)
- Reacts immediately to `NetworkChange.NetworkAvailabilityChanged`
- **Guest mode**: `SetCloudProbeEnabled(false)` — state is forced to `Unknown`, no HTTP request sent

**Enabling/disabling probing**:
- Called at startup from `App.axaml.cs` after resolving active username
- Called from `AppSettingsViewModel.OnHasActiveProfileChanged` when auth state changes (login/logout)

---

## Cloud Availability Rules

### `CanSyncToCloud` (AppSettingsViewModel)
```
HasActiveProfile && !IsSyncBusy && _connectivity.Snapshot.State == ConnectivityState.Online
```
Controls the 3 global sync action buttons (Restore, Push All, Process Queue).
Updated when: `HasActiveProfile` changes, `IsSyncBusy` changes, connectivity event fires.

### `CanRunSyncNow` / `CanRunCloudRepair` (RepositorySettingsViewModel)
```
HasCloudAccess && !IsSyncNowRunning
HasCloudAccess && !IsCloudRepairRunning
```
`HasCloudAccess` = active profile exists. Per-repo buttons check auth but NOT real-time connectivity — connectivity is validated inside `ResolveCloudConnectivityMessage()` at command execution time.

### `ShowConnectivityBanner` (AppSettings Sync tab)
```
HasActiveProfile && IsSyncTabSelected && (connectivity unavailable || sync paused)
```
Only shown to authenticated users on the Sync tab.

---

## Error Handling for Cloud Operations

### `OperationResult` error categories (`OperationErrorKind` enum)
```
None, General, NetworkUnavailable, CloudUnavailable, Timeout,
AuthenticationRequired, TokenExpired, Unauthorized,
NotFound, Conflict, ValidationFailed, FilesystemError, Fatal
```
Use `result.IsNetworkError` / `result.IsAuthError` for category-based handling.

### Post-auth cloud sync
After successful login/registration, the app attempts `RestoreRepositoriesFromCloudAsync` + `ProcessPendingQueueAsync`. If this throws:
- Auth is still considered successful (user is saved)
- `AuthMessage` shows `app_settings.auth_success_cloud_unavailable` note
- Cloud sync will resume automatically when connectivity is restored

### `ResolveCloudConnectivityMessage()` (AppSettingsViewModel)
Central helper used by all cloud commands. Returns a localized error string if:
- Guest mode → `app_settings.guest_auth_hint`
- Internet unavailable → `ui_error.internet_unavailable`
- Cloud unavailable → `ui_error.cloud_unavailable`
- Sync paused → `ui_error.cloud_actions_paused`
Returns `null` if cloud operations can proceed.

---

## Startup Flow (cloud portion)

```
RunDeferredStartupAsync()
  ├─ Query active username from DB
  ├─ connectivity.SetCloudProbeEnabled(activeUsername != null)   ← guest guard
  ├─ if no activeUsername → skip all cloud ops
  ├─ if activeUsername:
  │    ├─ Evaluate token validity
  │    ├─ if !CanUseForSync → skip cloud ops (log warning)
  │    └─ if CanUseForSync:
  │         ├─ RestoreRepositoriesFromCloudAsync()  ← try/catch logged
  │         └─ ProcessPendingQueueAsync()           ← try/catch logged
```

---

## What Works Offline (authenticated user)

| Operation | Online required? |
|-----------|-----------------|
| Open/browse repositories | ❌ No |
| Create manual snapshot | ❌ No |
| View file history + diffs | ❌ No |
| Run retention cleanup | ❌ No |
| Export snapshot bundle | ❌ No |
| Cloud push / restore | ✅ Yes |
| Token refresh | ✅ Yes |

---

## Key Files

| Concern | File |
|---------|------|
| Connectivity service | `src/Veyra.Desktop/Services/Connectivity/ConnectivityStatusService.cs` |
| Interface | `src/Veyra.Desktop/Services/Connectivity/IConnectivityStatusService.cs` |
| State model | `src/Veyra.Desktop/Services/Connectivity/Models/ConnectivityState.cs` |
| Auth guard | `src/Veyra.Desktop/ViewModels/Pages/Settings/AppSettingsViewModel.cs` → `ResolveCloudConnectivityMessage()` |
| Startup guest guard | `src/Veyra.Desktop/App.axaml.cs` → `RunDeferredStartupAsync()` |
| Error kind enum | `src/Veyra.Application/Common/Results/OperationErrorKind.cs` |
