using System.Text.Json;
using Azure.Storage.Queues;

namespace StatusMonitor.Core.Storage;

public interface IQueuePublisher
{
    Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default);
}

/// <summary>
/// Sends JSON messages as base64 so they are compatible with the Functions
/// queue-trigger default message encoding.
/// </summary>
public sealed class StorageQueuePublisher(QueueServiceClient queueService) : IQueuePublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync<T>(string queueName, T message, CancellationToken ct = default)
    {
        var queue = queueService.GetQueueClient(queueName);
        await queue.CreateIfNotExistsAsync(cancellationToken: ct);
        var payload = JsonSerializer.Serialize(message, SerializerOptions);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
        await queue.SendMessageAsync(encoded, cancellationToken: ct);
    }
}
