using Veyra.Domain.Entities;

namespace Veyra.Domain.Tests;

public class FileSnapshotTests
{
    [Fact]
    public void FileSnapshot_CanBeCreated()
    {
        var snapshot = new FileSnapshot
        {
            FilePath = "/test/path",
            ContentHash = "abc123",
            FileSize = 1024,
            CreatedAt = DateTime.UtcNow,
            IsDeleted = false
        };

        Assert.NotNull(snapshot);
        Assert.Equal("/test/path", snapshot.FilePath);
        Assert.Equal("abc123", snapshot.ContentHash);
        Assert.Equal(1024, snapshot.FileSize);
        Assert.False(snapshot.IsDeleted);
    }
}
