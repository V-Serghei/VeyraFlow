using MediatR;

namespace Veyra.Application.Commands.Repository;

public sealed record UnlinkFormatsFromRepositoryCommand(int RepositoryId, IReadOnlyCollection<string> FormatPatterns) : IRequest;
