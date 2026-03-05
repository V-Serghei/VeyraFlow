using Microsoft.EntityFrameworkCore;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;

namespace Veyra.Infrastructure.Data.Persistence;

public class VeyraDbContext : DbContext
{
    public VeyraDbContext(DbContextOptions<VeyraDbContext> options)
        : base(options) { }

    public DbSet<FileSnapshot> FileSnapshots { get; set; } = null!;
    public DbSet<WatchedDirectory> WatchedDirectories { get; set; } = null!;
    public DbSet<D_WatchedFormat> WatchedFormats { get; set; } = null!;
    public DbSet<WatchedDirectoryFormat> WatchedDirectoryFormats { get; set; } = null!;
    public DbSet<Repository> Repositories { get; set; } = null!;
    public DbSet<UserProfile> UserProfiles { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<D_WatchedFormat>().ToTable("D_WatchedFormats");

        modelBuilder.Entity<FileSnapshot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FilePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.ContentHash).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.ContentHash);
            entity.HasIndex(e => e.FilePath);
        });

        modelBuilder.Entity<WatchedDirectory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Path).IsRequired().HasMaxLength(2048);
            entity.HasIndex(e => e.Path).IsUnique();
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.ErrorMessage).HasMaxLength(1024);
        });

        modelBuilder.Entity<D_WatchedFormat>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Pattern).IsRequired().HasMaxLength(64);
            entity.HasIndex(e => e.Pattern).IsUnique();
        });

        modelBuilder.Entity<WatchedDirectoryFormat>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Directory)
                .WithMany(d => d.DirectoryFormats)
                .HasForeignKey(e => e.DirectoryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Format)
                .WithMany(f => f.DirectoryFormats)
                .HasForeignKey(e => e.FormatId);
            entity.HasIndex(e => new { e.DirectoryId, e.FormatId }).IsUnique();
        });

        modelBuilder.Entity<Repository>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.Description).HasMaxLength(1024);
            entity.HasOne(e => e.Directory)
                .WithOne()
                .HasForeignKey<Repository>(e => e.DirectoryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.DirectoryId).IsUnique();
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.Username).IsUnique();
        });
    }
}
