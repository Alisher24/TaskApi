using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskApi.Models;

namespace TaskApi.Tests.Integration;

public class CompletionSaveBarrier : SaveChangesInterceptor
{
    private readonly TaskCompletionSource _bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _participants;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<TaskItem>()
            .Any(entry => entry is { State: EntityState.Modified, Entity.IsCompleted: true }))
        {
            if (Interlocked.Increment(ref _participants) == 2)
            {
                _bothReady.TrySetResult();
            }
            await _bothReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }
}
