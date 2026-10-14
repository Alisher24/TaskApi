using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using TaskApi.Data;
using TaskApi.Messaging;

namespace TaskApi.Tests.Integration;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class TaskApiIntegrationTests
{
    private TaskApiFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _client = null!;
        _factory = new TaskApiFactory(IntegrationSettings.Load());
        await _factory.InitializeAsync();
        _client = _factory.CreateApiClient();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Test]
    public async Task CreateThenCompletePersistsTaskAndDeliversCompletionEvent()
    {
        // Arrange
        await using var queue = await TestEventQueue.CreateAsync(_factory.Settings);
        var before = DateTime.UtcNow;
        var request = new { title = "Интеграционный тест", priority = "High" };

        // Act
        using var created = await _client.PostAsJsonAsync("/tasks", request);
        var task = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = task.GetProperty("id").GetGuid();
        var afterCreation = DateTime.UtcNow;
        var eventAfterCreation = await queue.GetAsync();
        using var completed = await _client.PutAsync($"/tasks/{id}/complete", null);
        var completedTask = await completed.Content.ReadFromJsonAsync<JsonElement>();
        var completedAt = completedTask.GetProperty("completedAt").GetDateTime();
        var delivery = await queue.ReceiveAsync();
        using var message = JsonDocument.Parse(delivery.Body);
        var body = message.RootElement;
        var afterCompletion = DateTime.UtcNow;
        using var listed = await _client.GetAsync("/tasks");
        var tasks = await listed.Content.ReadFromJsonAsync<JsonElement[]>();
        var extraEvent = await queue.GetAsync();

        // Assert
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.Multiple(() =>
        {
            Assert.That(id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(created.Headers.Location?.OriginalString, Is.EqualTo($"/tasks/{id}"));
            Assert.That(task.GetProperty("isCompleted").GetBoolean(), Is.False);
            Assert.That(task.GetProperty("completedAt").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(task.GetProperty("createdAt").GetDateTime().Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(task.GetProperty("createdAt").GetDateTime(), Is.InRange(before, afterCreation));
            Assert.That(task.TryGetProperty("version", out _), Is.False);
        });
        Assert.That(eventAfterCreation, Is.Null, "Creation must not publish a completion event.");
        Assert.That(completed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(completedTask.GetProperty("isCompleted").GetBoolean(), Is.True);
            Assert.That(completedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(completedAt, Is.InRange(before, afterCompletion));
            Assert.That(body.EnumerateObject().Select(property => property.Name),
                Is.EquivalentTo(new[] { "taskId", "title", "completedAt", "priority" }));
            Assert.That(body.GetProperty("taskId").GetGuid(), Is.EqualTo(id));
            Assert.That(body.GetProperty("title").GetString(), Is.EqualTo("Интеграционный тест"));
            Assert.That(body.GetProperty("completedAt").GetDateTime(), Is.EqualTo(completedAt));
            Assert.That(body.GetProperty("priority").GetString(), Is.EqualTo("High"));
            Assert.That(delivery.Exchange, Is.EqualTo("task.events"));
            Assert.That(delivery.RoutingKey, Is.EqualTo("task.completed"));
            Assert.That(delivery.BasicProperties.ContentType, Is.EqualTo("application/json"));
            Assert.That(delivery.BasicProperties.Persistent, Is.True);
        });
        Assert.That(listed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(tasks, Has.Length.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(tasks![0].GetProperty("id").GetGuid(), Is.EqualTo(id));
            Assert.That(tasks[0].GetProperty("isCompleted").GetBoolean(), Is.True);
            Assert.That(tasks[0].GetProperty("completedAt").GetDateTime(),
                Is.EqualTo(completedAt).Within(TimeSpan.FromMicroseconds(1)));
        });
        Assert.That(extraEvent, Is.Null, "Completion must produce one event.");
    }

    [TestCase("{}", "title")]
    [TestCase("{\"title\":null}", "title")]
    [TestCase("{\"title\":\"\"}", "title")]
    [TestCase("{\"title\":\"   \"}", "title")]
    [TestCase("{\"title\":\"Valid\",\"priority\":3}", "priority")]
    [TestCase("{\"title\":\"Valid\",\"priority\":-1}", "priority")]
    [TestCase("{\"title\":\"Valid\",\"priority\":\"Urgent\"}", null)]
    [TestCase("{\"title\":\"Valid\",\"priority\":null}", null)]
    [TestCase("{", null)]
    public async Task InvalidCreationReturnsBadRequestWithoutPersistingTask(string json, string? errorField)
    {
        // Arrange
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        using var response = await _client.PostAsync("/tasks", content);
        JsonElement? problem = errorField is not null
            ? await response.Content.ReadFromJsonAsync<JsonElement>()
            : null;
        var tasks = await _client.GetFromJsonAsync<JsonElement[]>("/tasks");

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        if (errorField is not null)
        {
            Assert.Multiple(() =>
            {
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
                Assert.That(problem!.Value.GetProperty("errors").TryGetProperty(errorField, out _), Is.True);
            });
        }
        Assert.That(tasks, Is.Empty);
    }

    [Test]
    public async Task TitleLongerThan200CharactersIsRejected()
    {
        // Arrange
        var request = new { title = new string('a', 201) };

        // Act
        using var response = await _client.PostAsJsonAsync("/tasks", request);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var tasks = await _client.GetFromJsonAsync<JsonElement[]>("/tasks");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
            Assert.That(problem.GetProperty("errors").TryGetProperty("title", out _), Is.True);
            Assert.That(tasks, Is.Empty);
        });
    }

    [Test]
    public async Task CreationAccepts200CharacterTitleAndAllSupportedPriorities()
    {
        // Arrange
        var title = $" {new string('a', 198)} ";
        (object? Value, string Expected)[] priorities =
        [
            (null, "Medium"), ("Low", "Low"), ("Medium", "Medium"), ("High", "High"),
            (0, "Low"), (1, "Medium"), (2, "High")
        ];
        var requests = priorities.Select(priority =>
        {
            var request = new Dictionary<string, object> { ["title"] = title };
            if (priority.Value is not null)
            {
                request["priority"] = priority.Value;
            }
            return (Body: request, priority.Value, priority.Expected);
        }).ToArray();
        var results = new List<(HttpStatusCode StatusCode, JsonElement Task, object? Priority, string Expected)>();

        // Act
        foreach (var request in requests)
        {
            using var response = await _client.PostAsJsonAsync("/tasks", request.Body);
            var task = await response.Content.ReadFromJsonAsync<JsonElement>();
            results.Add((response.StatusCode, task, request.Value, request.Expected));
        }
        var tasks = await _client.GetFromJsonAsync<JsonElement[]>("/tasks");

        // Assert
        foreach (var result in results)
        {
            Assert.Multiple(() =>
            {
                Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.Created), $"Priority: {result.Priority ?? "default"}");
                Assert.That(result.Task.GetProperty("title").GetString(), Is.EqualTo(title));
                Assert.That(result.Task.GetProperty("priority").GetString(), Is.EqualTo(result.Expected));
            });
        }
        var expectedPriorities = results.ToDictionary(result => result.Task.GetProperty("id").GetGuid(), result => result.Expected);
        Assert.That(tasks, Has.Length.EqualTo(priorities.Length));
        foreach (var task in tasks!)
        {
            Assert.Multiple(() =>
            {
                Assert.That(task.GetProperty("title").GetString(), Is.EqualTo(title));
                Assert.That(task.GetProperty("priority").GetString(),
                    Is.EqualTo(expectedPriorities[task.GetProperty("id").GetGuid()]));
            });
        }
    }

    [Test]
    public async Task RepeatedCompletionReturnsConflictWithoutChangingDateOrPublishingAgain()
    {
        // Arrange
        await using var queue = await TestEventQueue.CreateAsync(_factory.Settings);
        var task = await CreateTaskAsync();
        var id = task.GetProperty("id").GetGuid();
        using var first = await _client.PutAsync($"/tasks/{id}/complete", null);
        await queue.ReceiveAsync();
        var before = (await _client.GetFromJsonAsync<JsonElement[]>("/tasks"))!.Single();

        // Act
        using var repeated = await _client.PutAsync($"/tasks/{id}/complete", null);
        var after = (await _client.GetFromJsonAsync<JsonElement[]>("/tasks"))!.Single();
        var extraEvent = await queue.GetAsync();

        // Assert
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.Multiple(() =>
        {
            Assert.That(after.GetProperty("isCompleted").GetBoolean(), Is.True);
            Assert.That(after.GetProperty("completedAt").GetDateTime(),
                Is.EqualTo(before.GetProperty("completedAt").GetDateTime()));
        });
        Assert.That(extraEvent, Is.Null);
    }

    [Test]
    public async Task ConcurrentCompletionReturnsOneSuccessOneConflictAndOneEvent()
    {
        // Arrange
        await using var queue = await TestEventQueue.CreateAsync(_factory.Settings);
        var barrier = new CompletionSaveBarrier();
        await using var concurrentFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddDbContext<TaskDbContext>(options => options.AddInterceptors(barrier))));
        using var client = concurrentFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        var task = await CreateTaskAsync(client);
        var id = task.GetProperty("id").GetGuid();

        // Act
        var responses = await Task.WhenAll(
            client.PutAsync($"/tasks/{id}/complete", null),
            client.PutAsync($"/tasks/{id}/complete", null));
        try
        {
            var success = await responses.Single(response => response.IsSuccessStatusCode)
                .Content.ReadFromJsonAsync<JsonElement>();
            var delivery = await queue.ReceiveAsync();
            using var message = JsonDocument.Parse(delivery.Body);
            var persisted = (await client.GetFromJsonAsync<JsonElement[]>("/tasks"))!.Single();
            var extraEvent = await queue.GetAsync();

            // Assert
            Assert.That(responses.Select(response => response.StatusCode),
                Is.EquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
            Assert.Multiple(() =>
            {
                Assert.That(persisted.GetProperty("isCompleted").GetBoolean(), Is.True);
                Assert.That(message.RootElement.GetProperty("taskId").GetGuid(), Is.EqualTo(id));
                Assert.That(message.RootElement.GetProperty("completedAt").GetDateTime(),
                    Is.EqualTo(success.GetProperty("completedAt").GetDateTime()));
                Assert.That(persisted.GetProperty("completedAt").GetDateTime(),
                    Is.EqualTo(success.GetProperty("completedAt").GetDateTime()).Within(TimeSpan.FromMicroseconds(1)));
            });
            Assert.That(extraEvent, Is.Null, "The losing completion must not publish an event.");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task UnavailableBrokerStillCompletesTaskAndLogsFailedEvent()
    {
        // Arrange
        // Keep a port reserved without listening, so connection attempts are refused deterministically.
        using var unavailableEndpoint = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailableEndpoint.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var logs = new TestLogProvider();
        await using var offlineFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<RabbitMqOptions>(options =>
                {
                    options.HostName = "127.0.0.1";
                    options.Port = ((IPEndPoint)unavailableEndpoint.LocalEndPoint!).Port;
                    options.PublishTimeoutSeconds = 1;
                });
                services.AddLogging(logging => logging.AddProvider(logs));
            }));
        using var client = offlineFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        client.Timeout = TimeSpan.FromSeconds(10);
        var task = await CreateTaskAsync(client);
        var id = task.GetProperty("id").GetGuid();

        // Act
        using var response = await client.PutAsync($"/tasks/{id}/complete", null);
        var completed = await response.Content.ReadFromJsonAsync<JsonElement>();
        var persisted = (await client.GetFromJsonAsync<JsonElement[]>("/tasks"))!.Single();
        var failure = logs.Entries.Single(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("event publication failed"));
        using var message = JsonDocument.Parse(failure.Message[failure.Message.IndexOf('{')..]);
        using var repeated = await client.PutAsync($"/tasks/{id}/complete", null);
        var publicationFailures = logs.Entries.Count(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("event publication failed"));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(completed.GetProperty("isCompleted").GetBoolean(), Is.True);
            Assert.That(persisted.GetProperty("isCompleted").GetBoolean(), Is.True);
            Assert.That(persisted.GetProperty("completedAt").GetDateTime(),
                Is.EqualTo(completed.GetProperty("completedAt").GetDateTime()).Within(TimeSpan.FromMicroseconds(1)));
            Assert.That(failure.Exception, Is.Not.Null);
            Assert.That(message.RootElement.GetProperty("taskId").GetGuid(), Is.EqualTo(id));
            Assert.That(message.RootElement.GetProperty("title").GetString(), Is.EqualTo("Task for completion"));
            Assert.That(message.RootElement.GetProperty("completedAt").GetDateTime(),
                Is.EqualTo(completed.GetProperty("completedAt").GetDateTime()));
            Assert.That(message.RootElement.GetProperty("priority").GetString(), Is.EqualTo("High"));
            Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(publicationFailures, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task InvalidAndMissingIdsReturnErrorsWithoutPublishing()
    {
        // Arrange
        await using var queue = await TestEventQueue.CreateAsync(_factory.Settings);
        var cases = new[]
        {
            (Method: HttpMethod.Put, Id: "not-a-guid", Expected: HttpStatusCode.BadRequest),
            (Method: HttpMethod.Put, Id: Guid.NewGuid().ToString(), Expected: HttpStatusCode.NotFound),
            (Method: HttpMethod.Delete, Id: "not-a-guid", Expected: HttpStatusCode.BadRequest),
            (Method: HttpMethod.Delete, Id: Guid.NewGuid().ToString(), Expected: HttpStatusCode.NotFound)
        };
        var results = new List<(HttpStatusCode StatusCode, HttpStatusCode Expected, string Description, JsonElement? Problem)>();

        // Act
        foreach (var testCase in cases)
        {
            var path = testCase.Method == HttpMethod.Put ? $"/tasks/{testCase.Id}/complete" : $"/tasks/{testCase.Id}";
            using var request = new HttpRequestMessage(testCase.Method, path);
            using var response = await _client.SendAsync(request);
            JsonElement? problem = testCase.Expected == HttpStatusCode.BadRequest
                ? await response.Content.ReadFromJsonAsync<JsonElement>()
                : null;
            results.Add((response.StatusCode, testCase.Expected, $"{testCase.Method} {path}", problem));
        }
        var tasks = await _client.GetFromJsonAsync<JsonElement[]>("/tasks");
        var completionEvent = await queue.GetAsync();

        // Assert
        foreach (var result in results)
        {
            Assert.That(result.StatusCode, Is.EqualTo(result.Expected), result.Description);
            if (result.Expected == HttpStatusCode.BadRequest)
            {
                Assert.That(result.Problem!.Value.GetProperty("errors").TryGetProperty("id", out _), Is.True);
            }
        }
        Assert.That(tasks, Is.Empty);
        Assert.That(completionEvent, Is.Null);
    }

    [Test]
    public async Task DeletedTaskDisappearsFromListAndCannotBeDeletedOrCompletedAgain()
    {
        // Arrange
        var task = await CreateTaskAsync();
        var id = task.GetProperty("id").GetGuid();

        // Act
        using var deleted = await _client.DeleteAsync($"/tasks/{id}");
        var tasksAfterDeletion = await _client.GetFromJsonAsync<JsonElement[]>("/tasks");
        using var repeated = await _client.DeleteAsync($"/tasks/{id}");
        using var completed = await _client.PutAsync($"/tasks/{id}/complete", null);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(tasksAfterDeletion, Is.Empty);
            Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(completed.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    private async Task<JsonElement> CreateTaskAsync(HttpClient? client = null)
    {
        using var response = await (client ?? _client).PostAsJsonAsync("/tasks",
            new { title = "Task for completion", priority = "High" });
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException($"Cannot arrange a task: expected 201 Created, received {response.StatusCode}.");
        }
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
