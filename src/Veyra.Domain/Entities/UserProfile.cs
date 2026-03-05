namespace Veyra.Domain.Entities;

public class UserProfile
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
