using Veyra.Application.DTOs;
using Veyra.Application.DTOs.Auth;

namespace Veyra.Application.Abstractions.Auth;

public interface IAccessTokenPolicyService
{
    public AccessTokenPolicyEvaluationDto Evaluate(string? accessToken, DateTime? nowUtc = null);
    public string GetPolicySummary();
}
