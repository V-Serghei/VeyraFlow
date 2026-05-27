using Veyra.Application.DTOs;

namespace Veyra.Application.Abstractions.Auth;

public interface IAccessTokenPolicyService
{
    AccessTokenPolicyEvaluationDto Evaluate(string? accessToken, DateTime? nowUtc = null);
    string GetPolicySummary();
}
