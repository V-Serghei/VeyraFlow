namespace Veyra.Application.Abstractions.Auth;

public interface IUserProfileRepository
{
    Task<string?> GetActiveUsernameAsync(CancellationToken ct = default);
    Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default);
}
