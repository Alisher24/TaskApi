using System.ComponentModel.DataAnnotations;

namespace TaskApi.Messaging;

public class RabbitMqOptions
{
    public const string SectionName = "RabbitMQ";

    [Required]
    public string HostName { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; }

    [Required]
    public string UserName { get; set; }

    [Required]
    public string Password { get; set; }

    [Required]
    public string VirtualHost { get; set; }

    [Range(1, 30)]
    public int PublishTimeoutSeconds { get; set; }

    [Range(3, 5)]
    public int ShutdownTimeoutSeconds { get; set; } = 5;
}
