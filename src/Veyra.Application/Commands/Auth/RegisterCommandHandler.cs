using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Auth;

public sealed class RegisterCommandHandler(
    IAuthService authService,
    IUserProfileRepository userProfileRepo,
    ILogger<RegisterCommandHandler> logger)
    : IRequestHandler<RegisterCommand, OperationResult>
{
    public async Task<OperationResult> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Attempting to register user {Username} with email {Email}", request.Username, request.Email);

        try
        {
            var session = await authService.RegisterAsync(request.Username, request.Email, request.Password, cancellationToken);
            if (session is null)
            {
                logger.LogWarning("Registration failed for user {Username}", request.Username);
                return OperationResult.Fail("Registration failed.");
            }

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

            logger.LogInformation("User {Username} registered and saved locally", session.Username);
            return OperationResult.Ok();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Registration rejected for user {Username}", request.Username);
            return OperationResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Registration failed for user {Username}", request.Username);
            return OperationResult.Fail("Registration failed.");
        }
    }
}
