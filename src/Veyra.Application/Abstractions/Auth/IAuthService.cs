using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Auth;

public interface IAuthService
{
    Task<AuthSessionDto?> LoginAsync(string username, string password, CancellationToken ct = default);
    Task<AuthSessionDto?> RegisterAsync(string username, string password, CancellationToken ct = default);
}
