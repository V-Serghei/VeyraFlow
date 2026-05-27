using System;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Auth;

public sealed class RegisterCommandHandler(
    IAuthService authService,
    IUserProfileRepository userProfileRepo,
    ILocalCredentialStore localCredentialStore,
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
            await localCredentialStore.SavePasswordAsync(session.Username, request.Password, cancellationToken);

            logger.LogInformation("User {Username} registered and saved locally", session.Username);
            return OperationResult.Ok();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Registration rejected for user {Username}", request.Username);
            return OperationResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            logger.LogWarning(ex, "Registration could not reach cloud for user {Username}", request.Username);

            if (ex.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult.Fail("Cloud service is temporarily unavailable. Try again later.");
            }

            return OperationResult.Fail("Internet connection is required. Connect to a network and try again.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Registration failed for user {Username}", request.Username);
            return OperationResult.Fail("Registration failed.");
        }
    }
}
