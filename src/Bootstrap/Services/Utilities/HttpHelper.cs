namespace Bootstrap.Services.Utilities;

public static class HttpHelper
{
    // This method waits for a service to be ready by polling its status.
    public static async Task<bool> WaitForService(
        HttpClient client,
        string url,
        TimeSpan timeout,
        params int[] extraAllowedStatusCodes)
    {
        Logging.Log.Information($"Waiting for {url} (timeout {timeout.TotalSeconds}s)");
        var startTime = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(10);

        while (true)
        {
            try
            {
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    Logging.Log.Information($"{url} is ready: {(int)response.StatusCode}");
                    return true;
                }

                // If extra allowed status codes were provided (e.g., TeamCity may return 503 or 401 during setup), treat them as ready
                if (extraAllowedStatusCodes != null
                    && extraAllowedStatusCodes.Contains((int)response.StatusCode))
                {
                    Logging.Log.Information(
                        $"{url} responded with {(int)response.StatusCode} which is allowed during startup; continuing");

                    return true;
                }

                // Got a response but not successful - log and continue waiting
                Logging.Log.Information(
                    $"{url} responded with {(int)response.StatusCode}, waiting for service to be fully ready...");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Logging.Log.Information($"Connection failed: {ex.Message}");
            }

            var elapsed = DateTime.UtcNow - startTime;
            if (elapsed > timeout)
            {
                Logging.Log.Error($"Timeout waiting for {url} after {(int)elapsed.TotalSeconds}s");
                return false;
            }

            if ((int)elapsed.TotalSeconds % 30 == 0)
            {
                Logging.Log.Information($"Still waiting for {url}... ({(int)elapsed.TotalSeconds}s elapsed)");
            }

            await Task.Delay(interval);
        }
    }
}