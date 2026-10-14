using TaskApi.Models;

namespace TaskApi.Contracts;

public record CreateTaskRequest(string? Title, Priority Priority = Priority.Medium);
