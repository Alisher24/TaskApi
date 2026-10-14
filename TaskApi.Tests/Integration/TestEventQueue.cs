using System.Diagnostics;
using RabbitMQ.Client;

namespace TaskApi.Tests.Integration;

public class TestEventQueue(IConnection connection, IChannel channel, string queueName) : IAsyncDisposable
{
    public static async Task<TestEventQueue> CreateAsync(IntegrationSettings settings)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var factory = new ConnectionFactory
        {
            HostName = settings.RabbitMq.HostName,
            Port = settings.RabbitMq.Port,
            UserName = settings.RabbitMq.UserName,
            Password = settings.RabbitMq.Password,
            VirtualHost = settings.RabbitMq.VirtualHost,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            ContinuationTimeout = TimeSpan.FromSeconds(5),
            AutomaticRecoveryEnabled = false,
            ClientProvidedName = "TaskApi integration test queue"
        };
        var connection = await factory.CreateConnectionAsync(timeout.Token);
        IChannel? channel = null;
        try
        {
            channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
            await channel.ExchangeDeclareAsync("task.events", ExchangeType.Topic, durable: true,
                autoDelete: false, cancellationToken: timeout.Token);
            var queue = await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true,
                autoDelete: true, cancellationToken: timeout.Token);
            await channel.QueueBindAsync(queue.QueueName, "task.events", "task.completed",
                cancellationToken: timeout.Token);
            return new TestEventQueue(connection, channel, queue.QueueName);
        }
        catch
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task<BasicGetResult?> GetAsync(CancellationToken cancellationToken = default) =>
        channel.BasicGetAsync(queueName, autoAck: true, cancellationToken);

    public async Task<BasicGetResult> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            var message = await GetAsync(timeout.Token);
            if (message is not null)
            {
                return message;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
        throw new TimeoutException("No task.completed event arrived in the test queue within 5 seconds.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await channel.DisposeAsync();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
