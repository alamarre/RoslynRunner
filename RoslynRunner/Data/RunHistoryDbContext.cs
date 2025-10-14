using Microsoft.EntityFrameworkCore;

namespace RoslynRunner.Data;

public class RunHistoryDbContext(DbContextOptions<RunHistoryDbContext> options) : DbContext(options)
{
    public const int MaxRecentRuns = 20;

    public DbSet<RunRecordEntity> RunRecords => Set<RunRecordEntity>();
    public DbSet<SavedRunEntity> SavedRuns => Set<SavedRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RunRecordEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.RunCommandJson).IsRequired();
            entity.Property(e => e.OutputJson).IsRequired();
            entity.Property(e => e.ErrorsJson).IsRequired();
        });

        modelBuilder.Entity<SavedRunEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.RunCommandJson).IsRequired();
        });
    }
}

public class RunRecordEntity
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public string RunCommandJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool Succeeded { get; set; }
    public string OutputJson { get; set; } = "[]";
    public string ErrorsJson { get; set; } = "[]";
}

public class SavedRunEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RunCommandJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
