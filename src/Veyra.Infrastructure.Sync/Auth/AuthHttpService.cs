using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Veyra.Application.Abstractions.Auth;

namespace Veyra.Infrastructure.Sync.Auth;

public sealed class AuthHttpService : IAuthService
{
    private readonly HttpClient _http;

    private sealed record LoginRequest(string Username, string Password);
    private sealed record LoginResponse(bool Ok, string? Message);

    public AuthHttpService(HttpClient http) => _http = http;

    public async Task<bool> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/login", new LoginRequest(username, password), ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized) return false;
        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct);
        return payload?.Ok == true;
    }
}
