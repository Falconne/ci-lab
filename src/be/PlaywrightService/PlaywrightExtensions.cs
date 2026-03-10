using Microsoft.Playwright;
using Serilog;

namespace PlaywrightService;

public static class PlaywrightExtensions
{
    public static async Task<int> CountWithRetry(this ILocator locator)
    {
        const int maxRetries = 2;
        var attempt = 0;

        while (true)
        {
            try
            {
                return await locator.CountAsync();
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt > maxRetries)
                {
                    Log.Error($"CountAsync failed after {maxRetries} retries: {ex.Message}");
                    throw;
                }

                Log.Warning($"CountAsync failed (attempt {attempt}/{maxRetries}), retrying: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }
}