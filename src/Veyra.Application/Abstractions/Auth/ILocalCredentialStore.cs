namespace Veyra.Application.Abstractions.Auth;

public interface ILocalCredentialStore
{
    Task SavePasswordAsync(string username, string password, CancellationToken ct = default);
    Task<bool> VerifyPasswordAsync(string username, string password, CancellationToken ct = default);
    Task<bool> HasPasswordAsync(string username, CancellationToken ct = default);
}
