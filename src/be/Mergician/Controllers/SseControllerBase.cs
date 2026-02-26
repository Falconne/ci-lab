using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Mergician.Controllers;

/// <summary>
///     Base controller providing Server-Sent Events (SSE) streaming helpers.
///     Handles SSE framing, heartbeat, and write locking so derived controllers
///     only need to provide the streaming logic.
/// </summary>
public abstract class SseControllerBase : ControllerBase
{
    protected static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Configures the response for SSE and runs the given stream writer with
    ///     heartbeat and write locking. Sends an 'event: done' frame on completion.
    /// </summary>
    protected async Task StreamSse(
        string streamName,
        Func<CancellationToken, SemaphoreSlim, Task> streamWriter,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        ConfigureSseHeaders();

        using var writeLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeat(heartbeatCts.Token, writeLock);

        try
        {
            await streamWriter(cancellationToken, writeLock);
            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
            await WriteSseRaw("event: done\ndata: {}\n\n", cancellationToken, writeLock);

            logger.LogInformation("SSE {StreamName} stream completed", streamName);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SSE {StreamName} stream cancelled by client", streamName);
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
        }
    }

    protected async Task WriteSseEvent(
        object payload,
        CancellationToken cancellationToken,
        SemaphoreSlim writeLock,
        string? eventName = null)
    {
        var json = JsonSerializer.Serialize(payload, SseJsonOptions);
        var frame = eventName == null
            ? $"data: {json}\n\n"
            : $"event: {eventName}\ndata: {json}\n\n";

        await WriteSseRaw(frame, cancellationToken, writeLock);
    }

    private void ConfigureSseHeaders()
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";
    }

    private async Task RunHeartbeat(CancellationToken cancellationToken, SemaphoreSlim writeLock)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await WriteSseRaw(": heartbeat\n\n", cancellationToken, writeLock);
        }
    }

    private async Task WriteSseRaw(string frame, CancellationToken cancellationToken, SemaphoreSlim writeLock)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await Response.WriteAsync(frame, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
