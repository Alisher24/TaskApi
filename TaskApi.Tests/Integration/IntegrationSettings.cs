using Microsoft.Extensions.Configuration;
using TaskApi.Messaging;

namespace TaskApi.Tests.Integration;

public record IntegrationSettings(string PostgresConnectionString, RabbitMqOptions RabbitMq)
{
    public static IntegrationSettings Load()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables("TASKAPI_TEST_")
            .Build();

        return new IntegrationSettings(
            configuration.GetConnectionString("TaskDb")
                ?? throw new InvalidOperationException("Test PostgreSQL connection string is missing."),
            configuration.GetSection("RabbitMQ").Get<RabbitMqOptions>()
                ?? throw new InvalidOperationException("Test RabbitMQ settings are missing."));
    }
}
