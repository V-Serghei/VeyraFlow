using MediatR;
using Microsoft.Extensions.Logging;
using Veyra.Application.Abstractions.Auth;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Auth;

public sealed class LoginCommandHandler : IRequestHandler<LoginCommand, OperationResult>
{
    public readonly IAuthService _authService;
    public readonly ILogger<LoginCommandHandler> _logger;
    public LoginCommandHandler(IAuthService authService, ILogger<LoginCommandHandler> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public Task<OperationResult> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Attempting to log in user {Username}", request.Username);
        return _authService.LoginAsync(request.Username, request.Password, cancellationToken)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    _logger.LogError(task.Exception, "Login failed for user {Username} due to an exception", request.Username);
                    return OperationResult.Fail("An error occurred during login.");
                }

                if (task.Result)
                {
                    _logger.LogInformation("User {Username} logged in successfully", request.Username);
                    return OperationResult.Ok();
                }
                else
                {
                    _logger.LogWarning("Login failed for user {Username} due to invalid credentials", request.Username);
                    return OperationResult.Fail("Invalid credentials.");
                }
            }, cancellationToken);
    }
}
