using MediatR;

namespace Veyra.Application.Commands.Repository;

public sealed record LinkFormatsToRepositoryCommand(int RepositoryId, IReadOnlyCollection<string> FormatPatterns) : IRequest;
