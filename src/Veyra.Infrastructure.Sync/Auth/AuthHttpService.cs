using System.Net;
using System.Net.Http.Json;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.DTOs;

namespace Veyra.Infrastructure.Sync.Auth;

public sealed class AuthHttpService : IAuthService
{
    private readonly HttpClient _http;

    private sealed record AuthRequest(string Username, string? Email, string Password);

    private sealed record AuthResponse(
        bool Ok,
        long UserId,
        string Username,
        string? Email,
        long? SessionId,
        string AccessToken,
        string? RefreshToken,
        bool IsNewUser,
        DateTime? ExpiresAtUtc,
        DateTime? RefreshExpiresAtUtc,
        string? Message);

    private sealed record RefreshRequest(string RefreshToken);

    public AuthHttpService(HttpClient http) => _http = http;

    public Task<AuthSessionDto?> LoginAsync(string username, string password, CancellationToken ct = default)
        => SendAuthAsync("/api/login", username, null, password, ct);

    public Task<AuthSessionDto?> RegisterAsync(string username, string email, string password, CancellationToken ct = default)
        => SendAuthAsync("/api/register", username, email, password, ct);

    public async Task<AuthSessionDto?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            "/api/refresh",
            new RefreshRequest(refreshToken),
            ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            return null;

        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: ct);
        if (payload is null || !payload.Ok || string.IsNullOrWhiteSpace(payload.AccessToken))
            return null;

        return new AuthSessionDto(
            payload.UserId,
            payload.Username,
            payload.Email,
            payload.AccessToken,
            payload.IsNewUser,
            payload.ExpiresAtUtc,
            payload.RefreshToken,
            payload.RefreshExpiresAtUtc,
            payload.SessionId);
    }

    public async Task<bool> LogoutAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/logout");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized)
            return false;

        if (!resp.IsSuccessStatusCode)
            return false;

        return true;
    }

    private async Task<AuthSessionDto?> SendAuthAsync(
        string endpoint,
        string username,
        string? email,
        string password,
        CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(
            endpoint,
            new AuthRequest(username, email, password),
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
            payload.Email,
            payload.AccessToken,
            payload.IsNewUser,
            payload.ExpiresAtUtc,
            payload.RefreshToken,
            payload.RefreshExpiresAtUtc,
            payload.SessionId);
    }
}
