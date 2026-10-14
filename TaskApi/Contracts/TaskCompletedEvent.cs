using TaskApi.Models;

namespace TaskApi.Contracts;

public record TaskCompletedEvent(
    Guid TaskId,
    string Title,
    DateTime CompletedAt,
    Priority Priority);
