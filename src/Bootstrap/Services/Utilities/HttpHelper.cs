using Serilog;

namespace Bootstrap.Utilities;

public static class HttpHelper
{
    /// <summary>
    /// Waits for a service to be ready by polling its status.
    /// </summary>
    public static async Task WaitForService(
        string url,
        TimeSpan timeout,
        params int[] extraAllowedStatusCodes)
    {
        Log.Information($"Waiting for {url} (timeout {timeout.TotalSeconds}s)");
        var startTime = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(10);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        while (true)
        {
            try
            {
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    Log.Information($"{url} is ready: {(int)response.StatusCode}");
                    return;
                }

                // If extra allowed status codes were provided, treat them as ready
                if (extraAllowedStatusCodes.Contains((int)response.StatusCode))
                {
                    Log.Information(
                        $"{url} responded with {(int)response.StatusCode} which is allowed during startup; continuing");
                    return;
                }

                Log.Information(
                    $"{url} responded with {(int)response.StatusCode}, waiting for service to be fully ready...");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Log.Information($"Connection failed: {ex.Message}");
            }

            var elapsed = DateTime.UtcNow - startTime;
            if (elapsed > timeout)
            {
                Log.Error($"Timeout waiting for {url} after {(int)elapsed.TotalSeconds}s");
                throw new InvalidOperationException($"Timeout waiting for {url} after {(int)elapsed.TotalSeconds}s");
            }

            if ((int)elapsed.TotalSeconds % 30 == 0)
            {
                Log.Information($"Still waiting for {url}... ({(int)elapsed.TotalSeconds}s elapsed)");
            }

            await Task.Delay(interval);
        }
    }
}
