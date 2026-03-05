namespace Veyra.Application.Abstractions.Auth;

public interface IUserProfileRepository
{
    Task SaveOrUpdateProfileAsync(string username, CancellationToken ct = default);
}

