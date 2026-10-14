using Microsoft.EntityFrameworkCore;
using TaskApi.Models;

namespace TaskApi.Data;

public class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskItem>();

        task.ToTable("Tasks");
        
        task.HasKey(item => item.Id);
        
        task.Property(item => item.Title)
            .IsRequired()
            .HasMaxLength(200);
        
        task.Property(item => item.IsCompleted)
            .HasDefaultValue(false);
        
        task.Property(item => item.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        
        task.Property(item => item.CompletedAt)
            .HasColumnType("timestamp with time zone");
        
        task.Property(item => item.Priority)
            .HasConversion<int>()
            .HasDefaultValue(Priority.Medium)
            .HasSentinel(Priority.Medium);
        
        task.Property(item => item.Version)
            .IsRowVersion();
    }
}
