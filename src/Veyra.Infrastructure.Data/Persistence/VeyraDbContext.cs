using Microsoft.EntityFrameworkCore;
using Veyra.Domain.Entities;
using Veyra.Domain.Entities.Watched;

namespace Veyra.Infrastructure.Data.Persistence;

public class VeyraDbContext : DbContext
{
    public VeyraDbContext(DbContextOptions<VeyraDbContext> options)
        : base(options) { }

    public DbSet<FileSnapshot> FileSnapshots { get; set; } = null!;
    public DbSet<FileIdentity> FileIdentities { get; set; } = null!;
    public DbSet<FileVersion> FileVersions { get; set; } = null!;
    public DbSet<FileVersionBlock> FileVersionBlocks { get; set; } = null!;
    public DbSet<SnapshotFileLink> SnapshotFileLinks { get; set; } = null!;

    public DbSet<WatchedDirectory> WatchedDirectories { get; set; } = null!;
    public DbSet<D_WatchedFormat> WatchedFormats { get; set; } = null!;
    public DbSet<WatchedDirectoryFormat> WatchedDirectoryFormats { get; set; } = null!;
    public DbSet<Repository> Repositories { get; set; } = null!;
    public DbSet<RepositorySnapshot> RepositorySnapshots { get; set; } = null!;
    public DbSet<RepositorySnapshotEntry> RepositorySnapshotEntries { get; set; } = null!;
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

        modelBuilder.Entity<FileIdentity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RelativePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(512);
            entity.Property(e => e.Extension).HasMaxLength(32);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();

            entity.HasOne(e => e.Repository)
                .WithMany(r => r.FileIdentities)
                .HasForeignKey(e => e.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.RepositoryId, e.RelativePath }).IsUnique();
            entity.HasIndex(e => new { e.RepositoryId, e.IsDeleted });
        });

        modelBuilder.Entity<FileVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContentHashSha256).IsRequired().HasMaxLength(64);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.LastWriteUtc).IsRequired();

            entity.HasOne(e => e.FileIdentity)
                .WithMany(i => i.Versions)
                .HasForeignKey(e => e.FileIdentityId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.FileIdentityId, e.CreatedAt });
            entity.HasIndex(e => e.ContentHashSha256);
        });

        modelBuilder.Entity<FileVersionBlock>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BlockHashBlake3).IsRequired().HasMaxLength(64);
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasOne(e => e.FileVersion)
                .WithMany(v => v.Blocks)
                .HasForeignKey(e => e.FileVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.FileVersionId, e.Sequence }).IsUnique();
            entity.HasIndex(e => e.BlockHashBlake3);
        });

        modelBuilder.Entity<SnapshotFileLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasOne(e => e.Snapshot)
                .WithMany(s => s.FileLinks)
                .HasForeignKey(e => e.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.FileIdentity)
                .WithMany(i => i.SnapshotLinks)
                .HasForeignKey(e => e.FileIdentityId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.FileVersion)
                .WithMany(v => v.SnapshotLinks)
                .HasForeignKey(e => e.FileVersionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.SnapshotId, e.FileIdentityId }).IsUnique();
            entity.HasIndex(e => new { e.FileIdentityId, e.SnapshotId });
            entity.HasIndex(e => e.FileVersionId);
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
            entity.Property(e => e.FileCount).HasDefaultValue(0);
            entity.Property(e => e.VersionCount).HasDefaultValue(0);
            entity.Property(e => e.TotalSizeBytes).HasDefaultValue(0L);
        });

        modelBuilder.Entity<RepositorySnapshot>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Trigger).IsRequired().HasMaxLength(64);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.Repository)
                .WithMany(r => r.Snapshots)
                .HasForeignKey(e => e.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.RepositoryId, e.CreatedAt });
        });

        modelBuilder.Entity<RepositorySnapshotEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RelativePath).IsRequired().HasMaxLength(2048);
            entity.Property(e => e.ParentRelativePath).HasMaxLength(2048);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(512);
            entity.Property(e => e.Extension).HasMaxLength(32);
            entity.Property(e => e.ContentHashSha256).HasMaxLength(64);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasOne(e => e.Snapshot)
                .WithMany(s => s.Entries)
                .HasForeignKey(e => e.SnapshotId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.SnapshotId, e.RelativePath }).IsUnique();
            entity.HasIndex(e => new { e.SnapshotId, e.ParentRelativePath });
            entity.HasIndex(e => new { e.RepositoryId, e.RelativePath });
            entity.HasIndex(e => e.ContentHashSha256);
        });

        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.Username).IsUnique();
        });
    }
}
