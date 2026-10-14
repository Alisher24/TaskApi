namespace TaskApi.Models;

public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Title { get; set; }

    public bool IsCompleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    public Priority Priority { get; set; } = Priority.Medium;

    public uint Version { get; set; }
}
