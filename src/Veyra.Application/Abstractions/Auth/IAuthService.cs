using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Auth;

public interface IAuthService
{
    Task<AuthSessionDto?> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<AuthSessionDto?> RegisterAsync(string username, string email, string password, CancellationToken ct = default);
    Task<AuthSessionDto?> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task<bool> LogoutAsync(string accessToken, CancellationToken ct = default);
}
