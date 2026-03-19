using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.DTOs;

namespace Veyra.Infrastructure.Sync.Auth;

public sealed class AuthHttpService : IAuthService
{
    private readonly HttpClient _http;
    private readonly ILogger<AuthHttpService> _log;

    public AuthHttpService(HttpClient http, ILogger<AuthHttpService> log)
    {
        _http = http;
        _log = log;
    }

    public Task<AuthSessionDto?> LoginAsync(string username, string password, CancellationToken ct = default)
        => SendAuthAsync("/api/login", username, null, password, ct);

    public Task<AuthSessionDto?> RegisterAsync(string username, string email, string password, CancellationToken ct = default)
        => SendAuthAsync("/api/register", username, email, password, ct);

    public async Task<AuthSessionDto?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        _log.LogInformation("Cloud auth refresh started.");
        using var resp = await _http.PostAsJsonAsync(
            "/api/refresh",
            new RefreshRequest(refreshToken),
            ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            _log.LogWarning(
                "Cloud auth refresh rejected. StatusCode {StatusCode}. ResponseBody {ResponseBody}",
                (int)resp.StatusCode,
                await ReadResponseBodySafeAsync(resp, ct));
            return null;
        }

        await EnsureSuccessAsync(resp, "refresh", ct);

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
        _log.LogInformation("Cloud auth logout started.");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/logout");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized)
        {
            _log.LogWarning("Cloud auth logout rejected with unauthorized.");
            return false;
        }

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning(
                "Cloud auth logout failed. StatusCode {StatusCode}. ResponseBody {ResponseBody}",
                (int)resp.StatusCode,
                await ReadResponseBodySafeAsync(resp, ct));
            return false;
        }

        _log.LogInformation("Cloud auth logout completed.");
        return true;
    }

    private async Task<AuthSessionDto?> SendAuthAsync(
        string endpoint,
        string username,
        string? email,
        string password,
        CancellationToken ct)
    {
        _log.LogInformation(
            "Cloud auth request started. Endpoint {Endpoint}. Username {Username}. HasEmail {HasEmail}",
            endpoint,
            username,
            !string.IsNullOrWhiteSpace(email));
        using var resp = await _http.PostAsJsonAsync(
            endpoint,
            new AuthRequest(username, email, password),
            ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
        {
            var responseBody = await ReadResponseBodySafeAsync(resp, ct);
            _log.LogWarning(
                "Cloud auth request rejected. Endpoint {Endpoint}. Username {Username}. StatusCode {StatusCode}. ResponseBody {ResponseBody}",
                endpoint,
                username,
                (int)resp.StatusCode,
                responseBody);

            var userMessage = TryExtractUserMessage(responseBody);
            if (!string.IsNullOrWhiteSpace(userMessage))
                throw new InvalidOperationException(userMessage);

            return null;
        }

        await EnsureSuccessAsync(resp, endpoint, ct);

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

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await ReadResponseBodySafeAsync(response, ct);
        _log.LogWarning(
            "Cloud auth request failed. Operation {Operation}. StatusCode {StatusCode}. ReasonPhrase {ReasonPhrase}. ResponseBody {ResponseBody}",
            operation,
            (int)response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            body);

        throw new HttpRequestException(
            $"Cloud auth {operation} failed with status {(int)response.StatusCode} ({response.ReasonPhrase ?? "unknown"}). Response: {body}",
            null,
            response.StatusCode);
    }

    private static async Task<string> ReadResponseBodySafeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return "(empty)";

            return body.Length <= 2048 ? body : body[..2048];
        }
        catch (Exception ex)
        {
            return $"(failed to read response body: {ex.Message})";
        }
    }

    private static string? TryExtractUserMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody) ||
            string.Equals(responseBody, "(empty)", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("message", out var messageElement))
                return null;

            var message = messageElement.GetString();
            return string.IsNullOrWhiteSpace(message)
                ? null
                : message.Trim();
        }
        catch
        {
            return null;
        }
    }
}
