using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Mergician.Controllers;

// TODO: Rather than making this a base class, make it a helper service that others can use with composition.
/// <summary>
///     Base controller providing Server-Sent Events (SSE) streaming helpers.
///     Handles SSE framing so derived controllers only need to provide the streaming logic.
/// </summary>
public abstract class SseControllerBase : ControllerBase
{
    protected static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    ///     Configures the response for SSE and runs the given stream writer.
    ///     Sends an 'event: done' frame on completion.
    /// </summary>
    protected async Task StreamSse(
        string streamName,
        Func<CancellationToken, Task> streamWriter,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        ConfigureSseHeaders();

        try
        {
            await streamWriter(cancellationToken);
            await WriteSseRaw("event: done\ndata: {}\n\n", cancellationToken);

            logger.LogInformation("SSE {StreamName} stream completed", streamName);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SSE {StreamName} stream cancelled by client", streamName);
        }
    }

    protected async Task WriteSseEvent(
        object payload,
        CancellationToken cancellationToken,
        string? eventName = null)
    {
        var json = JsonSerializer.Serialize(payload, SseJsonOptions);
        var frame = eventName == null
            ? $"data: {json}\n\n"
            : $"event: {eventName}\ndata: {json}\n\n";

        await WriteSseRaw(frame, cancellationToken);
    }

    private void ConfigureSseHeaders()
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";
    }

    private async Task WriteSseRaw(string frame, CancellationToken cancellationToken)
    {
        await Response.WriteAsync(frame, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}