using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TaskApi.Data;
using TaskApi.Endpoints;
using TaskApi.Messaging;
using TaskApi.Models;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TaskDb")
    ?? throw new InvalidOperationException("Connection string 'TaskDb' is not configured.");
builder.Services.AddDbContext<TaskDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<Priority>()));
builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<ITaskEventPublisher, RabbitMqTaskEventPublisher>();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapTaskEndpoints();

app.Run();
