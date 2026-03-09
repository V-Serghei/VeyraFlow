using System.Net;
using System.Net.Http.Json;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.DTOs;

namespace Veyra.Infrastructure.Sync.Auth;

public sealed class AuthHttpService : IAuthService
{
    private readonly HttpClient _http;

    private sealed record AuthRequest(string Username, string Password);

    private sealed record AuthResponse(
        bool Ok,
        long UserId,
        string Username,
        string AccessToken,
        bool IsNewUser,
        string? Message);

    public AuthHttpService(HttpClient http) => _http = http;

    public Task<AuthSessionDto?> LoginAsync(string username, string password, CancellationToken ct = default)
        => SendAuthAsync("/api/login", username, password, ct);

    public Task<AuthSessionDto?> RegisterAsync(string username, string password, CancellationToken ct = default)
        => SendAuthAsync("/api/register", username, password, ct);

    private async Task<AuthSessionDto?> SendAuthAsync(
        string endpoint,
        string username,
        string password,
        CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(
            endpoint,
            new AuthRequest(username, password),
            ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
            return null;

        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: ct);
        if (payload is null || !payload.Ok || string.IsNullOrWhiteSpace(payload.AccessToken))
            return null;

        return new AuthSessionDto(
            payload.UserId,
            payload.Username,
            payload.AccessToken,
            payload.IsNewUser);
    }
}
