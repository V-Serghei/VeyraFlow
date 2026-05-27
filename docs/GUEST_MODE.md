# Guest Mode

Guest mode is the state in which VeyraFlow operates when there is no active user profile. All local features remain fully available; all cloud features are hidden and no HTTP requests to cloud endpoints are made.

---

## Definition

Guest mode is determined by a single condition: there is no active row in the `user_profiles` SQLite table (`HasActiveProfile = false` in `AppSettingsViewModel`).

The computed property `IsGuestMode` in `AppSettingsViewModel` is simply `!HasActiveProfile`. It is re-evaluated whenever the `HasActiveProfile` backing field changes.

There is no separate "guest account" entity. The user can transition out of guest mode at any time by signing in through the auth dialog, which creates a `UserProfile` row and sets it active.

---

## Features Available in Guest Mode

All local features work without any cloud dependency:

| Feature | Available in guest mode |
|---|---|
| Repository creation and management | Yes |
| Manual snapshots | Yes |
| Automatic scheduled snapshots | Yes — scheduler runs regardless of auth state |
| File history and diffs | Yes |
| Retention policy execution | Yes — engine runs fully locally; no cloud dependency |
| Retention policy UI | Visible in Professional mode; hidden in Basic mode (same as authenticated users) |
| Transient cache cleanup | Yes |
| System monitoring | Yes |
| Application settings (language, theme, experience mode, autostart, etc.) | Yes |
| Export bundles | Yes |

---

## Features Disabled or Hidden in Guest Mode

| Feature | How it is suppressed |
|---|---|
| Cloud sync buttons | Hidden via `IsVisible` bindings on `HasActiveProfile` |
| Connectivity banner | Gated on `HasActiveProfile`; not shown to guests |
| Dashboard cloud panel | Hidden when `HasActiveProfile = false` |
| Per-repository "Sync now" and "Cloud repair" | Disabled; `ResolveCloudConnectivityMessage` returns the `app_settings.guest_auth_hint` string which blocks the action |
| Processing the pending sync queue | Guarded by `ResolveCloudConnectivityMessage`; returns immediately for guests |
| Cloud storage metrics section | `ShowCloudStorageDiagnostics = IsProfessionalMode && HasActiveProfile` |
| Technical cloud details | `ShowTechnicalCloudDetails = IsProfessionalMode && HasActiveProfile` |
| Sensitive action verification toggle | `CanEditSensitiveActionVerification = HasActiveProfile` |

---

## What Must Not Happen in Guest Mode

These behaviors are explicitly prevented:

- **Cloud warnings or "server unavailable" messages** are not shown to guests. The UI sections that produce them are hidden.
- **Background HTTP probing to the cloud endpoint** is disabled. `SetCloudProbeEnabled(false)` is called when no active username is found at startup, preventing `ConnectivityStatusService` from sending any HTTP requests.

---

## Implementation Details

### SetCloudProbeEnabled

Called from `App.axaml.cs` (`RunDeferredStartupAsync`) after checking for an active username:

```csharp
connectivityService.SetCloudProbeEnabled(!string.IsNullOrWhiteSpace(activeUsername));
```

When `false` is passed:
- `ConnectivityStatusService` sets the internal `_cloudProbeEnabled` flag to `false`.
- The state is immediately forced to `Unknown` and `StatusChanged` is fired.
- The polling loop's `ProbeAsync` method returns `Unknown` without making any network call.

When the user later signs in and `HasActiveProfile` becomes `true`, `OnHasActiveProfileChanged` in `AppSettingsViewModel` calls `SetCloudProbeEnabled(true)`, which allows the polling loop to resume.

### ResolveCloudConnectivityMessage

Called before every cloud operation in `AppSettingsViewModel`:

```csharp
private string? ResolveCloudConnectivityMessage()
    => !HasActiveProfile
        ? Loc.T("app_settings.guest_auth_hint")
        : _cloudSyncRuntime.IsPaused
        ? Loc.T("ui_error.cloud_actions_paused")
        : _connectivity.Snapshot.State switch
    {
        ConnectivityState.InternetUnavailable => Loc.T("ui_error.internet_required"),
        ConnectivityState.CloudUnavailable => Loc.T("ui_error.cloud_temporarily_unavailable"),
        _ => null
    };
```

A non-null return value means the operation cannot proceed. For guests the returned string is the `app_settings.guest_auth_hint` localization key ("In guest mode, cloud features are hidden. Sign in to enable sync, cloud restore, and protected actions."). A null return means the operation can proceed.

### CanSyncToCloud

```csharp
public bool CanSyncToCloud
    => HasActiveProfile
       && !IsSyncBusy
       && _connectivity.Snapshot.State == ConnectivityState.Online;
```

This is always `false` in guest mode because `HasActiveProfile` is `false`. Controls bound to `CanSyncToCloud` are automatically disabled.

---

## Guest Mode + Retention

Retention policy runs fully locally and has no awareness of auth state. The `EfRepositoryRetentionService` does not check `HasActiveProfile` or `ConnectivityStatusService`. Scheduled retention cleanup runs on its configured interval regardless of whether the user is signed in. This is intentional: local data management must not depend on cloud availability.

In Basic experience mode, the retention policy UI section is not shown (it requires Professional mode). However, the engine still executes any previously configured retention settings in the background.

---

## Guest Mode + Offline

Guest mode and offline mode are effectively identical from the cloud perspective: neither makes any cloud requests. If a user is signed out and also offline, all local features remain available. The connectivity state is `Unknown` (probing disabled), not `InternetUnavailable`, because the probe never runs to discover the actual network state.

---

## Sync Section UI Text

The sync section in App Settings shows different text depending on auth state. In guest mode:

- `SyncSectionIntroText` resolves to `app_settings.sync_intro_guest`: "You are using guest mode right now. Cloud features stay hidden until you sign in."
- `SyncSectionHelpText` resolves to `app_settings.sync_help_guest`: "After you sign in, this section will show cloud restore, repository upload, and queue management."

---

## Related Source Files

- `src/Veyra.Desktop/ViewModels/Pages/Settings/AppSettingsViewModel.cs` — `HasActiveProfile`, `IsGuestMode`, `CanSyncToCloud`, `ResolveCloudConnectivityMessage`, `OnHasActiveProfileChanged`
- `src/Veyra.Desktop/Services/Connectivity/ConnectivityStatusService.cs` — `SetCloudProbeEnabled`
- `src/Veyra.Desktop/App.axaml.cs` — `RunDeferredStartupAsync` (guest detection at startup)
- `src/Veyra.Infrastructure.Data/Setup/Retention/EfRepositoryRetentionService.cs`
- `src/Veyra.Desktop/Localization/en.json` — `app_settings.guest_auth_hint`, `app_settings.sync_intro_guest`, `app_settings.sync_help_guest`
