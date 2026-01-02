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

                LogHelper.LogInfo($"Waiting {delay}ms before retry...", 1);
                await Task.Delay(delay);
            }
        }

        return null;
    }

    public static async Task<bool> RetryUntilSuccess(
        Func<Task<bool>> operation,
        int maxAttempts = 3,
        int baseDelayMs = 2000,
        bool useExponentialBackoff = true)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var success = await operation();
            if (success)
            {
                return true;
            }

            if (attempt < maxAttempts)
            {
                var delay = useExponentialBackoff
                    ? Math.Min(baseDelayMs * (int)Math.Pow(2, attempt - 1), 10000)
                    : baseDelayMs;

                LogHelper.LogInfo($"Waiting {delay}ms before retry...", 1);
                await Task.Delay(delay);
            }
        }

        return false;
    }
}