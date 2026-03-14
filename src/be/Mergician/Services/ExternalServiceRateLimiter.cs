using System.Threading.RateLimiting;

namespace Mergician.Services;

/// <summary>
///     Enforces a global rate limit on outbound calls to external services.
/// </summary>
public sealed class ExternalServiceRateLimiter : IDisposable
{
    private const int _maxCallsPerSecond = 100;

    private static readonly TimeSpan _logThrottleWindow = TimeSpan.FromSeconds(30);

    private readonly ILogger<ExternalServiceRateLimiter> _logger;

    // Guards _pendingHitCount and _lastLogTime only; not on the hot path.
    private readonly Lock _logLock = new();

    private readonly TokenBucketRateLimiter _rateLimiter;

    private DateTime _lastLogTime = DateTime.MinValue;

    private int _pendingHitCount;

    public ExternalServiceRateLimiter(ILogger<ExternalServiceRateLimiter> logger)
    {
        _logger = logger;
        _rateLimiter = new TokenBucketRateLimiter(
            new TokenBucketRateLimiterOptions
            {
                TokenLimit = _maxCallsPerSecond,
                TokensPerPeriod = _maxCallsPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10_000,
                AutoReplenishment = true
            });
    }

    public void Dispose()
    {
        _rateLimiter.Dispose();
    }

    /// <summary>
    ///     Waits until a rate-limit token is available before the caller may proceed.
    ///     If the call had to wait because the rate limit was active, an error is logged
    ///     (throttled to at most once per 30-second window) with the number of calls throttled
    ///     in that window.
    /// </summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        // Fast path: token available immediately, no throttling.
        using var instantLease = _rateLimiter.AttemptAcquire();
        if (instantLease.IsAcquired)
        {
            return;
        }

        // Rate limit active: record the hit and log if the throttle window has expired.
        RecordRateLimitHit();

        // Queue the call and wait for a token to become available.
        using var queuedLease = await _rateLimiter.AcquireAsync(1, cancellationToken);
        if (!queuedLease.IsAcquired)
        {
            // Only reachable if the queue is full or the token was cancelled.
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "External service rate limiter queue is full. Too many concurrent requests.");
        }
    }

    private void RecordRateLimitHit()
    {
        lock (_logLock)
        {
            _pendingHitCount++;
            var now = DateTime.UtcNow;
            if (now - _lastLogTime >= _logThrottleWindow)
            {
                _logger.LogError(
                    "GitLab API rate limit of {MaxCallsPerSecond} calls/second exceeded. "
                    + "{Count} call(s) were queued waiting for a token in the last {Window} seconds.",
                    _maxCallsPerSecond,
                    _pendingHitCount,
                    (int)_logThrottleWindow.TotalSeconds);

                _pendingHitCount = 0;
                _lastLogTime = now;
            }
        }
    }
}