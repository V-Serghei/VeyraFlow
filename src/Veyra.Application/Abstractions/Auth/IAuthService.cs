using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Auth;

namespace Veyra.Application.Abstractions.Auth;

public interface IAuthService
{
    public Task<AuthSessionDto?> LoginAsync(string username, string password, CancellationToken ct = default);
    public Task<AuthSessionDto?> RegisterAsync(string username, string email, string password, CancellationToken ct = default);
    public Task<AuthSessionDto?> RefreshAsync(string refreshToken, CancellationToken ct = default);
    public Task<bool> LogoutAsync(string accessToken, CancellationToken ct = default);
}
