using System.Text.Json;

namespace Mergician.Services;

/// <summary>
///     Helper service providing Server-Sent Events (SSE) streaming functionality.
///     Inject into controllers to add SSE streaming support via composition.
/// </summary>
public class SseService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    ///     Configures the response for SSE and runs the given stream writer.
    ///     Sends an 'event: done' frame on completion.
    /// </summary>
    public async Task StreamSse(
        HttpResponse response,
        string streamName,
        Func<CancellationToken, Task> streamWriter,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        ConfigureSseHeaders(response);

        try
        {
            await streamWriter(cancellationToken);
            await WriteSseRaw(response, "event: done\ndata: {}\n\n", cancellationToken);

            logger.LogInformation("SSE {StreamName} stream completed", streamName);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SSE {StreamName} stream cancelled by client", streamName);
        }
    }

    /// <summary>
    ///     Serializes the payload and writes an SSE event frame to the response.
    /// </summary>
    public async Task WriteSseEvent(
        HttpResponse response,
        object payload,
        CancellationToken cancellationToken,
        string? eventName = null)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var frame = eventName == null
            ? $"data: {json}\n\n"
            : $"event: {eventName}\ndata: {json}\n\n";

        await WriteSseRaw(response, frame, cancellationToken);
    }

    private static void ConfigureSseHeaders(HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteSseRaw(HttpResponse response, string frame, CancellationToken cancellationToken)
    {
        await response.WriteAsync(frame, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
