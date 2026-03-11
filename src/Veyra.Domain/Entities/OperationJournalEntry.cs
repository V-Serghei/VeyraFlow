namespace Veyra.Domain.Entities;

public class OperationJournalEntry
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public required string Level { get; set; }
    public required string Category { get; set; }
    public required string Action { get; set; }
    public int? RepositoryId { get; set; }
    public string? Username { get; set; }
    public required string Message { get; set; }
    public string? Details { get; set; }
}
