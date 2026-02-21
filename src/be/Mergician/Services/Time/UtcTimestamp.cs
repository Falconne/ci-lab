namespace Mergician.Services.Time;

public static class UtcTimestamp
{
    public static DateTime EnsureUtc(DateTime timestamp, string context, ILogger logger)
    {
        switch (timestamp.Kind)
        {
            case DateTimeKind.Utc:
                return timestamp;

            case DateTimeKind.Local:
            {
                var converted = timestamp.ToUniversalTime();
                logger.LogInformation(
                    "{Context} provided a local timestamp; normalized to UTC {TimestampUtc}",
                    context,
                    converted);

                return converted;
            }

            default:
                logger.LogError(
                    "{Context} provided a timestamp with unspecified kind; cannot safely normalize to UTC",
                    context);

                throw new InvalidOperationException(
                    $"{context} timestamp kind is Unspecified. All backend and DB timestamps must be UTC.");
        }
    }

    public static DateTimeOffset EnsureUtc(DateTimeOffset timestamp, string context, ILogger logger)
    {
        if (timestamp.Offset == TimeSpan.Zero)
        {
            return timestamp;
        }

        var converted = timestamp.ToUniversalTime();
        logger.LogInformation(
            "{Context} provided offset {Offset}; normalized to UTC {TimestampUtc}",
            context,
            timestamp.Offset,
            converted);

        return converted;
    }
}
