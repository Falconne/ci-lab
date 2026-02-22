namespace Mergician.Services.Time;

// TODO make the methods that take `string context` instead take a `Func<string>` so that the context string is only created
// by the caller if logging is actually needed.
public static class UtcTimestamp
{
    public static DateTime EnsureUtc(DateTime timestamp, Func<string> contextProvider, ILogger logger)
    {
        // Don't construct the context string unless a log entry will actually be written.
        switch (timestamp.Kind)
        {
            case DateTimeKind.Utc:
                return timestamp;

            case DateTimeKind.Local:
                {
                    var converted = timestamp.ToUniversalTime();
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "{Context} provided a local timestamp; normalized to UTC {TimestampUtc}",
                            contextProvider(),
                            converted);
                    }

                    return converted;
                }

            default:
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError(
                        "{Context} provided a timestamp with unspecified kind; cannot safely normalize to UTC",
                        contextProvider());
                }

                throw new InvalidOperationException(
                    $"{contextProvider()} timestamp kind is Unspecified. All backend and DB timestamps must be UTC.");
        }
    }

    public static DateTimeOffset EnsureUtc(DateTimeOffset timestamp, Func<string> contextProvider, ILogger logger)
    {
        if (timestamp.Offset == TimeSpan.Zero)
        {
            return timestamp;
        }

        var converted = timestamp.ToUniversalTime();
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "{Context} provided offset {Offset}; normalized to UTC {TimestampUtc}",
                contextProvider(),
                timestamp.Offset,
                converted);
        }

        return converted;
    }
}
