using MediatR;

namespace Veyra.Application.Queries.Repository;

public sealed record GetTrackedExtensionsQuery() : IRequest<List<string>>;
