using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using TaskApi.Contracts;
using TaskApi.Data;
using TaskApi.Models;

namespace TaskApi.Endpoints;

public static class TaskEndpoints
{
    public static RouteGroupBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/tasks");

        group.MapPost("", CreateTask).WithName("CreateTask");
        group.MapGet("", GetTasks).WithName("GetTasks");
        group.MapPut("/{id}/complete", CompleteTask).WithName("CompleteTask");
        group.MapDelete("/{id}", DeleteTask).WithName("DeleteTask");

        return group;
    }

    private static async Task<Results<Created<TaskResponse>, ValidationProblem>> CreateTask(
        CreateTaskRequest request,
        TaskDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors["title"] = ["Title is required and must not contain only whitespace."];
        }
        else if (request.Title.Length > 200)
        {
            errors["title"] = ["Title must not exceed 200 characters."];
        }

        if (!Enum.IsDefined(request.Priority))
        {
            errors["priority"] = ["Priority must be Low, Medium or High (0, 1 or 2)."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var task = new TaskItem
        {
            Title = request.Title!,
            Priority = request.Priority
        };

        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/tasks/{task.Id}", TaskResponse.FromTask(task));
    }

    private static async Task<Ok<TaskResponse[]>> GetTasks(
        TaskDbContext db,
        CancellationToken cancellationToken)
    {
        var tasks = await db.Tasks.AsNoTracking().ToListAsync(cancellationToken);
        return TypedResults.Ok(tasks.Select(TaskResponse.FromTask).ToArray());
    }

    private static async Task<Results<Ok<TaskResponse>, NotFound, Conflict, ValidationProblem>> CompleteTask(
        string id,
        TaskDbContext db,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var taskId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["id"] = ["Id must be a valid Guid."]
            });
        }

        var task = await db.Tasks.SingleOrDefaultAsync(task => task.Id == taskId, cancellationToken);

        if (task is null)
        {
            return TypedResults.NotFound();
        }

        if (task.IsCompleted)
        {
            return TypedResults.Conflict();
        }

        task.IsCompleted = true;
        task.CompletedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Conflict();
        }

        return TypedResults.Ok(TaskResponse.FromTask(task));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> DeleteTask(
        string id,
        TaskDbContext db,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var taskId))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["id"] = ["Id must be a valid Guid."]
            });
        }

        var deletedCount = await db.Tasks
            .Where(task => task.Id == taskId)
            .ExecuteDeleteAsync(cancellationToken);

        return deletedCount == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }
}
