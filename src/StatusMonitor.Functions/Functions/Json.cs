using System.Text.Json;

namespace StatusMonitor.Functions.Functions;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, Options)
        ?? throw new InvalidOperationException($"Queue message could not be parsed as {typeof(T).Name}: {payload}");
}
