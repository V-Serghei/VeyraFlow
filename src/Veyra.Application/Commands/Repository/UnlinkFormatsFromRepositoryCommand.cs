using MediatR;

namespace Veyra.Application.Commands.Repository;

public abstract record UnlinkFormatsFromRepositoryCommand(int RepositoryId, IReadOnlyCollection<string> FormatPatterns) : IRequest;
