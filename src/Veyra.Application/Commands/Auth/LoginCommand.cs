using MediatR;
using Veyra.Application.Common.Results;

namespace Veyra.Application.Commands.Auth;

public sealed record LoginCommand(string Username, string Password) : IRequest<OperationResult>;
