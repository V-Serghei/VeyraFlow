using Microsoft.EntityFrameworkCore;
using Veyra.Domain.Entities;

namespace Veyra.Infrastructure.Data.Persistence;

public class VeyraDbContext : DbContext
{
    public VeyraDbContext(DbContextOptions<VeyraDbContext> options)
        : base(options)
    {

    }

    public DbSet<FileSnapshot> FileSnapshots { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FileSnapshot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.ContentHash).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.FilePath);
        });
    }
}
