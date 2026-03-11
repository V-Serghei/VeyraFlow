namespace Veyra.Domain.Entities;

public class UserProfile
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public string? Email { get; set; }
    public long? CloudUserId { get; set; }
    public long? CloudSessionId { get; set; }
    public string? AccessToken { get; set; }
    public DateTime? AccessTokenExpiresAtUtc { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiresAtUtc { get; set; }
    public bool RequirePasswordForSensitiveActions { get; set; }
    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
