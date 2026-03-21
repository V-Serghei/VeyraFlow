using System;
using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Auth;

public sealed class LoginCommandHandler(
    IAuthService authService,
    IUserProfileRepository userProfileRepo,
    ILocalCredentialStore localCredentialStore,
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
            await localCredentialStore.SavePasswordAsync(session.Username, request.Password, cancellationToken);

            logger.LogInformation("User profile saved locally for {Username}", session.Username);
            return OperationResult.Ok();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Login rejected for user {Username}", request.Username);
            return OperationResult.Fail(ex.Message);
        }
        catch (Exception ex) when (IsConnectivityFailure(ex))
        {
            logger.LogWarning(ex, "Cloud login failed because connectivity is unavailable for {Username}", request.Username);

            var hasLocalCredential = await localCredentialStore.HasPasswordAsync(request.Username, cancellationToken);
            if (hasLocalCredential)
            {
                var isPasswordValid = await localCredentialStore.VerifyPasswordAsync(request.Username, request.Password, cancellationToken);
                if (!isPasswordValid)
                    return OperationResult.Fail("Invalid credentials.");

                var activated = await userProfileRepo.SetActiveProfileAsync(request.Username, cancellationToken);
                if (activated)
                {
                    logger.LogInformation("Offline local sign-in succeeded for {Username}", request.Username);
                    return OperationResult.Ok();
                }

                return OperationResult.Fail("Offline sign-in is available only for profiles that already exist on this device.");
            }

            return OperationResult.Fail(DescribeConnectivityFailure(ex));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Login failed for user {Username} due to an exception", request.Username);
            return OperationResult.Fail("An error occurred during login.");
        }
    }

    private static bool IsConnectivityFailure(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or TimeoutException;

    private static string DescribeConnectivityFailure(Exception ex)
    {
        if (ex.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloud service is temporarily unavailable. Try again later.";
        }

        return "Internet connection is required. Connect to a network and try again.";
    }
}
