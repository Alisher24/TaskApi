using TaskApi.Models;

namespace TaskApi.Contracts;

public record TaskResponse(
    Guid Id,
    string Title,
    bool IsCompleted,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    Priority Priority)
{
    public static TaskResponse FromTask(TaskItem task) => new(
        task.Id,
        task.Title,
        task.IsCompleted,
        task.CreatedAt,
        task.CompletedAt,
        task.Priority);
}
