namespace Veyra.Application.Abstractions.Auth;

public interface ILocalCredentialStore
{
    public Task SavePasswordAsync(string username, string password, CancellationToken ct = default);
    public Task<bool> VerifyPasswordAsync(string username, string password, CancellationToken ct = default);
    public Task<bool> HasPasswordAsync(string username, CancellationToken ct = default);
}
