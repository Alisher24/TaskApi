using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TaskApi.Contracts;

namespace TaskApi.Messaging;

public sealed class RabbitMqTaskEventPublisher : ITaskEventPublisher, IAsyncDisposable
{
    public const string ExchangeName = "task.events";
    public const string RoutingKey = "task.completed";

    private readonly ConnectionFactory _connectionFactory;
    private readonly TimeSpan _publishTimeout;
    private readonly ILogger<RabbitMqTaskEventPublisher> _logger;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public RabbitMqTaskEventPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTaskEventPublisher> logger)
    {
        var settings = options.Value;
        _publishTimeout = TimeSpan.FromSeconds(settings.PublishTimeoutSeconds);
        _logger = logger;
        _connectionFactory = new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port,
            UserName = settings.UserName,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost,
            ClientProvidedName = "TaskApi publisher",
            RequestedConnectionTimeout = _publishTimeout,
            ContinuationTimeout = _publishTimeout,
            AutomaticRecoveryEnabled = false
        };
    }

    public async Task PublishAsync(TaskCompletedEvent message, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, TaskEventJson.Options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_publishTimeout);

        await _channelLock.WaitAsync(timeout.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var channel = await GetChannelAsync(timeout.Token);
            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Persistent = true,
                Type = RoutingKey
            };

            await channel.BasicPublishAsync(
                exchange: ExchangeName,
                routingKey: RoutingKey,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: timeout.Token);
        }
        catch
        {
            await DisposeConnectionAsync();
            throw;
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
        {
            return _channel;
        }

        await DisposeConnectionAsync();
        _connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        await _channel.ExchangeDeclareAsync(
            exchange: ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        return _channel;
    }

    private async Task DisposeConnectionAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;

        if (channel is not null)
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to dispose the RabbitMQ channel.");
            }
        }

        if (connection is not null)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to dispose the RabbitMQ connection.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channelLock.WaitAsync();
        try
        {
            _disposed = true;
            await DisposeConnectionAsync();
        }
        finally
        {
            _channelLock.Release();
        }
    }
}
