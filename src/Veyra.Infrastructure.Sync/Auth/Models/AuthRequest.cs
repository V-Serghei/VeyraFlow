namespace Veyra.Infrastructure.Sync.Auth;

internal sealed record AuthRequest(string Username, string? Email, string Password);
