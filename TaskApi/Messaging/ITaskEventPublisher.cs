using TaskApi.Contracts;

namespace TaskApi.Messaging;

public interface ITaskEventPublisher
{
    Task PublishAsync(TaskCompletedEvent message, CancellationToken cancellationToken = default);
}
