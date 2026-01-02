namespace Bootstrap.Services.Utilities;

public static class HttpHelper
{
    public static async Task<bool> WaitForService(
        HttpClient client,
        string url,
        TimeSpan timeout,
        params int[] extraAllowedStatusCodes)
    {
        LogHelper.Log($"Waiting for {url} (timeout {timeout.TotalSeconds}s)");
        var startTime = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(10);

        while (true)
        {
            try
            {
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    LogHelper.Log($"{url} is ready: {(int)response.StatusCode}");
                    return true;
                }

                // If extra allowed status codes were provided (e.g., TeamCity may return 503 or 401 during setup), treat them as ready
                if (extraAllowedStatusCodes != null
                    && extraAllowedStatusCodes.Contains((int)response.StatusCode))
                {
                    LogHelper.LogInfo(
                        $"{url} responded with {(int)response.StatusCode} which is allowed during startup; continuing",
                        1);

                    return true;
                }

                // Got a response but not successful - log and continue waiting
                LogHelper.LogInfo(
                    $"{url} responded with {(int)response.StatusCode}, waiting for service to be fully ready...",
                    1);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                LogHelper.LogInfo($"Connection failed: {ex.Message}", 1);
            }

            var elapsed = DateTime.UtcNow - startTime;
            if (elapsed > timeout)
            {
                LogHelper.LogError($"Timeout waiting for {url} after {(int)elapsed.TotalSeconds}s");
                return false;
            }

            if ((int)elapsed.TotalSeconds % 30 == 0)
            {
                LogHelper.Log($"Still waiting for {url}... ({(int)elapsed.TotalSeconds}s elapsed)");
            }

            await Task.Delay(interval);
        }
    }
}