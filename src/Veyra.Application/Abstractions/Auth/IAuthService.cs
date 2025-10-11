namespace Veyra.Application.Abstractions.Auth;

public interface IAuthService
{
    Task<bool> LoginAsync(string username, string password, CancellationToken ct = default);
}
