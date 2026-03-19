namespace Veyra.Desktop.ViewModels.Pages.Settings;

public sealed record AppArtifactKeyItemViewModel(
    string KeyId,
    string StatusCode,
    string StatusText,
    string CreatedAtText,
    string RotatedAtText,
    string RevokedAtText,
    string NoteText,
    bool IsActive,
    bool CanRevoke,
    bool IsRetired,
    bool IsRevoked,
    bool HasRotatedAt,
    bool HasRevokedAt);
