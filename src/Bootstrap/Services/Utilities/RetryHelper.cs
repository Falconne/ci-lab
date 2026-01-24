namespace Bootstrap.Services.Utilities;

public static class RetryHelper
{
    public static async Task<T?> Retry<T>(
        Func<Task<T?>> operation,
        int maxAttempts = 3,
        int baseDelayMs = 2000,
        bool useExponentialBackoff = true) where T : class
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var result = await operation();
            if (result != null)
            {
                return result;
            }

            if (attempt < maxAttempts)
            {
                var delay = useExponentialBackoff
                    ? Math.Min(baseDelayMs * (int)Math.Pow(2, attempt - 1), 10000)
                    : baseDelayMs;

                Logging.Log.Information($"Waiting {delay}ms before retry...");
                await Task.Delay(delay);
            }
        }

        return null;
    }
}