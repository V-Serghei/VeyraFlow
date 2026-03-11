using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Auth;

public sealed class LoginCommandHandler(
    IAuthService authService,
    IUserProfileRepository userProfileRepo,
    ILogger<LoginCommandHandler> logger)
    : IRequestHandler<LoginCommand, OperationResult>
{
    public async Task<OperationResult> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Attempting to log in user {Username}", request.Username);

        try
        {
            var session = await authService.LoginAsync(request.Username, request.Password, cancellationToken);

            if (session is null)
            {
                logger.LogWarning("Login failed for user {Username}: invalid credentials", request.Username);
                return OperationResult.Fail("Invalid credentials.");
            }

            logger.LogInformation("User {Username} logged in successfully", request.Username);

            await userProfileRepo.SaveOrUpdateProfileAsync(
                session.Username,
                session.CloudUserId,
                session.AccessToken,
                session.Email,
                session.CloudSessionId,
                session.RefreshToken,
                session.AccessTokenExpiresAtUtc,
                session.RefreshTokenExpiresAtUtc,
                cancellationToken);

            logger.LogInformation("User profile saved locally for {Username}", session.Username);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Login failed for user {Username} due to an exception", request.Username);
            return OperationResult.Fail("An error occurred during login.");
        }
    }
}
