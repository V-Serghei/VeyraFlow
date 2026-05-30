using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Auth;

namespace Veyra.Infrastructure.Sync.Auth;

public sealed class AccessTokenPolicyService(IConfiguration config) : IAccessTokenPolicyService
{
    private readonly TimeSpan _expiringSoonWindow = TimeSpan.FromMinutes(
        Math.Clamp(config.GetValue<int?>("CloudApi:TokenPolicy:ExpiringSoonMinutes") ?? 15, 1, 24 * 60));

    private readonly TimeSpan _clockSkew = TimeSpan.FromSeconds(
        Math.Clamp(config.GetValue<int?>("CloudApi:TokenPolicy:ClockSkewSeconds") ?? 60, 0, 300));

    public AccessTokenPolicyEvaluationDto Evaluate(string? accessToken, DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new AccessTokenPolicyEvaluationDto(
                AccessTokenValidityState.Missing,
                null,
                null,
                CanUseForSync: false,
                Description: "No active token. Sign in is required.");
        }

        if (!TryReadExpirationUtc(accessToken, out var expiresAtUtc))
        {
            return new AccessTokenPolicyEvaluationDto(
                AccessTokenValidityState.Invalid,
                null,
                null,
                CanUseForSync: false,
                Description: "Token format is invalid. Sign in again.");
        }

        var remaining = expiresAtUtc - now;

        if (remaining <= -_clockSkew)
        {
            return new AccessTokenPolicyEvaluationDto(
                AccessTokenValidityState.Expired,
                expiresAtUtc,
                remaining,
                CanUseForSync: false,
                Description: $"Token expired at {expiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}. Sign in again.");
        }

        if (remaining <= _expiringSoonWindow)
        {
            return new AccessTokenPolicyEvaluationDto(
                AccessTokenValidityState.ExpiringSoon,
                expiresAtUtc,
                remaining,
                CanUseForSync: true,
                Description: $"Token expires soon ({Math.Max(0, (int)Math.Ceiling(remaining.TotalMinutes))} min left). Consider re-login.");
        }

        return new AccessTokenPolicyEvaluationDto(
            AccessTokenValidityState.Valid,
            expiresAtUtc,
            remaining,
            CanUseForSync: true,
            Description: $"Token valid until {expiresAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}.");
    }

    public string GetPolicySummary()
        => $"Token warning window: {_expiringSoonWindow.TotalMinutes:0} min. Allowed clock skew: {_clockSkew.TotalSeconds:0} sec.";

    private static bool TryReadExpirationUtc(string accessToken, out DateTime expiresAtUtc)
    {
        expiresAtUtc = default;

        var parts = accessToken.Split('.');
        if (parts.Length != 3)
            return false;

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);

            if (!doc.RootElement.TryGetProperty("exp", out var expElement))
                return false;

            if (!expElement.TryGetInt64(out var epochSeconds) || epochSeconds <= 0)
                return false;

            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string input)
    {
        var normalized = input.Replace('-', '+').Replace('_', '/');

        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        return Convert.FromBase64String(normalized);
    }
}
