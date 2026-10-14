using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TaskApi.Contracts;

namespace TaskApi.Messaging;

public sealed class RabbitMqTaskEventPublisher : ITaskEventPublisher, IHostedService, IAsyncDisposable
{
    public const string ExchangeName = "task.events";
    public const string RoutingKey = "task.completed";

    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(1);
    private readonly IConnectionFactory _connectionFactory;
    private readonly TimeSpan _publishTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger<RabbitMqTaskEventPublisher> _logger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly TaskCompletionSource _publicationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _stoppingRegistration;
    private IConnection? _connection;
    private IChannel? _channel;
    private int _activePublications;
    private bool _stopping;
    private Task? _shutdownTask;

    public RabbitMqTaskEventPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTaskEventPublisher> logger,
        IHostApplicationLifetime applicationLifetime)
        : this(CreateConnectionFactory(options.Value), options, logger, applicationLifetime)
    {
    }

    internal RabbitMqTaskEventPublisher(
        IConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTaskEventPublisher> logger,
        IHostApplicationLifetime applicationLifetime)
    {
        _connectionFactory = connectionFactory;
        _publishTimeout = TimeSpan.FromSeconds(options.Value.PublishTimeoutSeconds);
        _shutdownTimeout = TimeSpan.FromSeconds(options.Value.ShutdownTimeoutSeconds);
        _logger = logger;
        _applicationLifetime = applicationLifetime;
    }

    private static ConnectionFactory CreateConnectionFactory(RabbitMqOptions settings)
    {
        var publishTimeout = TimeSpan.FromSeconds(settings.PublishTimeoutSeconds);
        return new ConnectionFactory
        {
            HostName = settings.HostName,
            Port = settings.Port,
            UserName = settings.UserName,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost,
            ClientProvidedName = "TaskApi publisher",
            RequestedConnectionTimeout = publishTimeout,
            ContinuationTimeout = publishTimeout,
            AutomaticRecoveryEnabled = false
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration = _applicationLifetime.ApplicationStopping.Register(() => { BeginShutdown(); });
        return Task.CompletedTask;
    }

    public async Task PublishAsync(TaskCompletedEvent message, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("The RabbitMQ publisher is stopping and no longer accepts events.");
            }

            _activePublications++;
        }

        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message, TaskEventJson.Options);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCancellation.Token);
            timeout.CancelAfter(_publishTimeout);

            await _channelLock.WaitAsync(timeout.Token);
            try
            {
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
                if (!_shutdownCancellation.IsCancellationRequested)
                {
                    await DisposeConnectionAsync();
                }

                throw;
            }
            finally
            {
                _channelLock.Release();
            }
        }
        finally
        {
            lock (_stateLock)
            {
                if (--_activePublications == 0 && _stopping)
                {
                    _publicationsDrained.TrySetResult();
                }
            }
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
            {
                return _channel;
            }
        }

        await DisposeConnectionAsync();
        var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        bool accepted;
        lock (_stateLock)
        {
            accepted = !cancellationToken.IsCancellationRequested;
            if (accepted)
            {
                _connection = connection;
            }
        }

        if (!accepted)
        {
            await DisposeResourcesAsync(null, connection);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        lock (_stateLock)
        {
            accepted = !cancellationToken.IsCancellationRequested && ReferenceEquals(_connection, connection);
            if (accepted)
            {
                _channel = channel;
            }
        }

        if (!accepted)
        {
            await DisposeResourcesAsync(channel, null);
            throw new OperationCanceledException(cancellationToken);
        }

        await channel.ExchangeDeclareAsync(
            exchange: ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);

        return channel;
    }

    private Task BeginShutdown()
    {
        lock (_stateLock)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }

            _stopping = true;
            if (_activePublications == 0)
            {
                _publicationsDrained.TrySetResult();
            }

            _shutdownCancellation.CancelAfter(_shutdownTimeout);
            return _shutdownTask = ShutdownAsync();
        }
    }

    private async Task ShutdownAsync()
    {
        await Task.Yield();
        _logger.LogInformation("RabbitMQ publisher is stopping. Drain timeout: {TimeoutSeconds} seconds.",
            _shutdownTimeout.TotalSeconds);

        try
        {
            await _publicationsDrained.Task.WaitAsync(_shutdownCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("RabbitMQ publication drain was cancelled. Remaining sends will be aborted.");
        }

        await _shutdownCancellation.CancelAsync();
        await DisposeConnectionAsync();
        _logger.LogInformation("RabbitMQ publisher stopped.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var shutdown = BeginShutdown();
        using var registration = cancellationToken.Register(() => _shutdownCancellation.Cancel());
        await shutdown;
    }

    private Task DisposeConnectionAsync()
    {
        IChannel? channel;
        IConnection? connection;
        lock (_stateLock)
        {
            channel = _channel;
            connection = _connection;
            _channel = null;
            _connection = null;
        }

        return DisposeResourcesAsync(channel, connection);
    }

    private async Task DisposeResourcesAsync(IChannel? channel, IConnection? connection)
    {
        if (channel is { IsOpen: true })
        {
            await CleanupAsync(() => channel.CloseAsync(200, "Publisher stopping", abort: true), "close channel");
        }

        if (connection is { IsOpen: true })
        {
            await CleanupAsync(() => connection.CloseAsync(200, "Publisher stopping", CleanupTimeout, abort: true),
                "close connection");
        }

        if (channel is not null)
        {
            await CleanupAsync(() => channel.DisposeAsync().AsTask(), "dispose channel");
        }

        if (connection is not null)
        {
            await CleanupAsync(() => connection.DisposeAsync().AsTask(), "dispose connection");
        }
    }

    private async Task CleanupAsync(Func<Task> cleanup, string operation)
    {
        try
        {
            await cleanup().WaitAsync(CleanupTimeout);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to {Operation} for RabbitMQ.", operation);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await BeginShutdown();
        _stoppingRegistration.Dispose();
    }
}
