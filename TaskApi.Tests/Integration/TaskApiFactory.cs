using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TaskApi.Data;
using TaskApi.Messaging;

namespace TaskApi.Tests.Integration;

public class TaskApiFactory(IntegrationSettings settings) : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"taskapi_tests_{Guid.NewGuid():N}";
    private bool _databaseCreated;

    public IntegrationSettings Settings { get; } = settings;

    private string AdminConnectionString => new NpgsqlConnectionStringBuilder(Settings.PostgresConnectionString)
    {
        Pooling = false,
        Timeout = 5,
        CommandTimeout = 10
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaskDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<TaskDbContext>>();
            var connectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString)
            {
                Database = _databaseName
            }.ConnectionString;
            services.AddDbContext<TaskDbContext>(options => options.UseNpgsql(connectionString));
            services.PostConfigure<RabbitMqOptions>(options =>
            {
                options.HostName = Settings.RabbitMq.HostName;
                options.Port = Settings.RabbitMq.Port;
                options.UserName = Settings.RabbitMq.UserName;
                options.Password = Settings.RabbitMq.Password;
                options.VirtualHost = Settings.RabbitMq.VirtualHost;
                options.PublishTimeoutSeconds = Settings.RabbitMq.PublishTimeoutSeconds;
                options.ShutdownTimeoutSeconds = Settings.RabbitMq.ShutdownTimeoutSeconds;
            });
        });
    }

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
        await command.ExecuteNonQueryAsync();
        _databaseCreated = true;

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TaskDbContext>().Database.MigrateAsync();
    }

    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (_databaseCreated)
            {
                await using var connection = new NpgsqlConnection(AdminConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
                await command.ExecuteNonQueryAsync();
                _databaseCreated = false;
            }
        }
    }
}
