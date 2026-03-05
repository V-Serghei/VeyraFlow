using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Auth;

public sealed class LoginCommandHandler(
    IAuthService authService,
    ILogger<LoginCommandHandler> logger)
    : IRequestHandler<LoginCommand, OperationResult>
{
    public async Task<OperationResult> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Attempting to log in user {Username}", request.Username);

        try
        {
            var success = await authService.LoginAsync(request.Username, request.Password, cancellationToken);

            if (success)
            {
                logger.LogInformation("User {Username} logged in successfully", request.Username);
                return OperationResult.Ok();
            }

            logger.LogWarning("Login failed for user {Username}: invalid credentials", request.Username);
            return OperationResult.Fail("Invalid credentials.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Login failed for user {Username} due to an exception", request.Username);
            return OperationResult.Fail("An error occurred during login.");
        }
    }
}
