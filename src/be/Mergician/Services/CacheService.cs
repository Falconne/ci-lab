namespace Mergician.Services;

/// <summary>
///     A thread-safe, time-based cache that maps keys to values.
///     The entire cache is cleared when the configured expiration time elapses since the last reset.
/// </summary>
public class CacheService<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _cache = new();

    private readonly TimeSpan _expiration;

    private readonly Lock _lock = new();

    private readonly ILogger<CacheService<TKey, TValue>> _logger;

    private DateTime _lastReset = DateTime.UtcNow;

    public CacheService(ILogger<CacheService<TKey, TValue>> logger, TimeSpan? expiration = null)
    {
        _logger = logger;
        _expiration = expiration ?? TimeSpan.FromHours(24);
    }

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_lock)
        {
            if (IsExpired())
            {
                _logger.LogDebug("Cache expired, clearing all entries");
                _cache.Clear();
                _lastReset = DateTime.UtcNow;
                value = default;
                return false;
            }

            return _cache.TryGetValue(key, out value);
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (IsExpired())
            {
                _logger.LogDebug("Cache expired on write, clearing all entries");
                _cache.Clear();
                _lastReset = DateTime.UtcNow;
            }

            _cache[key] = value;
        }
    }

    private bool IsExpired()
    {
        return DateTime.UtcNow - _lastReset > _expiration;
    }
}