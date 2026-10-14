using System.Text.Json;
using System.Text.Json.Serialization;
using TaskApi.Models;

namespace TaskApi.Messaging;

public static class TaskEventJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<Priority>() }
    };
}
