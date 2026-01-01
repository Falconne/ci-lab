using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Bootstrap.Services.Utilities;

public static class HttpHelper
{
    public static async Task<bool> WaitForServiceAsync(HttpClient client, string url, TimeSpan timeout)
    {
        Console.WriteLine($"[bootstrap] Waiting for {url} (timeout {timeout.TotalSeconds}s)");
        var startTime = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(10);

        while (true)
        {
            try
            {
                var response = await client.GetAsync(url);
                Console.WriteLine($"[bootstrap] {url} responded: {(int)response.StatusCode}");
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                var elapsed = DateTime.UtcNow - startTime;
                if (elapsed > timeout)
                {
                    Console.Error.WriteLine($"[bootstrap] ERROR: Timeout waiting for {url}: {ex.Message}");
                    return false;
                }

                if ((int)elapsed.TotalSeconds % 30 == 0)
                {
                    Console.WriteLine($"[bootstrap] Still waiting for {url}... ({(int)elapsed.TotalSeconds}s elapsed)");
                }

                await Task.Delay(interval);
            }
        }
    }
}
